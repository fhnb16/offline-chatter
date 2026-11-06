using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Engine;

public enum InstallStage
{
    Downloading,
    Extracting,
    Testing,
    Done,
}

/// <summary>
/// Установка варианта llama.cpp: загрузка zip (+ cudart для CUDA) с проверкой SHA-256 из GitHub API,
/// распаковка во временную папку, smoke-тест, перенос в engine/&lt;tag&gt;-&lt;backend&gt;/.
/// </summary>
public sealed class EngineInstaller
{
    private readonly AppPaths _paths;
    private readonly DownloadManager _downloads;
    private readonly GitHubReleases _releases;

    public EngineInstaller(AppPaths paths, DownloadManager downloads, GitHubReleases releases)
    {
        _paths = paths;
        _downloads = downloads;
        _releases = releases;
    }

    public string TargetDirectory(EngineRelease release, EngineVariant variant) =>
        Path.Combine(_paths.Engine, $"{release.Tag}-{variant.Key}");

    public async Task<(EngineInstall install, SmokeResult smoke)> InstallAsync(EngineRelease release, EngineVariant variant,
        IProgress<InstallStage>? progress, CancellationToken ct)
    {
        var target = TargetDirectory(release, variant);
        var downloadsDir = Path.Combine(_paths.Cache, "downloads");
        Directory.CreateDirectory(downloadsDir);

        progress?.Report(InstallStage.Downloading);
        var main = Enqueue(variant.Asset, downloadsDir, release.Tag);
        var runtime = variant.CudaRuntime != null ? Enqueue(variant.CudaRuntime, downloadsDir, release.Tag) : null;

        using (ct.Register(() =>
               {
                   _downloads.Cancel(main);
                   if (runtime != null) _downloads.Cancel(runtime);
               }))
        {
            if (!await main.WhenFinished.ConfigureAwait(false))
                throw new IOException(main.Error ?? "Download canceled");
            if (runtime != null && !await runtime.WhenFinished.ConfigureAwait(false))
                throw new IOException(runtime.Error ?? "Download canceled");
        }
        ct.ThrowIfCancellationRequested();

        progress?.Report(InstallStage.Extracting);
        var temp = Path.Combine(_paths.Engine, ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            await Task.Run(() =>
            {
                ExtractFlattened(main.TargetPath, temp);
                if (runtime != null) ExtractFlattened(runtime.TargetPath, temp);
            }, ct).ConfigureAwait(false);

            progress?.Report(InstallStage.Testing);
            var smoke = await CompatChecker.SmokeTestAsync(Path.Combine(temp, "llama-server.exe")).ConfigureAwait(false);

            EngineRegistry.WriteManifest(temp, new EngineManifest
            {
                Tag = release.Tag,
                Backend = variant.Backend,
                BackendVersion = variant.BackendVersion,
                Arch = variant.Arch,
                VersionText = smoke.VersionText,
                Build = smoke.Build ?? release.BuildNumber,
            });

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
            Log.Info("Engine", "Installed " + Path.GetFileName(target), new { smoke.Health, smoke.ExitCode });

            // Архивы больше не нужны
            TryDelete(main.TargetPath);
            if (runtime != null) TryDelete(runtime.TargetPath);

            progress?.Report(InstallStage.Done);
            var manifest = EngineRegistry.ReadManifest(target)!;
            return (new EngineInstall(target, manifest, false), smoke);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                try { Directory.Delete(temp, true); }
                catch (IOException ex) { Log.Warn("Engine", "Cannot remove temp folder " + temp, ex); }
            }
        }
    }

    private DownloadJob Enqueue(ReleaseAsset asset, string dir, string tag) =>
        _downloads.Enqueue(new DownloadJob(_releases.MirrorUrl(asset.DownloadUrl), Path.Combine(dir, asset.Name), asset.Name)
        {
            ExpectedSize = asset.Size > 0 ? asset.Size : null,
            ExpectedSha256 = asset.Sha256,
            Group = "engine:" + tag,
        });

    /// <summary>
    /// Распаковка с защитой от путей вида "..\" (zip slip). Если весь архив лежит в одной папке верхнего уровня —
    /// её содержимое поднимается на уровень выше, чтобы llama-server.exe оказался в корне.
    /// </summary>
    public static void ExtractFlattened(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        using var zip = ZipFile.OpenRead(zipPath);

        string? commonPrefix = null;
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            var top = slash > 0 ? name.Substring(0, slash + 1) : null;
            if (top == null || (commonPrefix != null && commonPrefix != top))
            {
                commonPrefix = null;
                break;
            }
            commonPrefix = top;
        }

        foreach (var e in zip.Entries)
        {
            var rel = e.FullName.Replace('\\', '/');
            if (commonPrefix != null) rel = rel.Substring(commonPrefix.Length);
            if (rel.Length == 0) continue;
            var full = Path.GetFullPath(Path.Combine(destination, rel.Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe path in archive: " + e.FullName);
            if (rel.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            e.ExtractToFile(full, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
