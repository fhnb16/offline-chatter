using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Engine;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class EngineUpdateTests
{
    private static EngineRelease Release() =>
        GitHubReleases.Parse(JToken.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "release-b12176.json"))));

    private static EngineInstall Install(string name, int build, EngineBackend backend = EngineBackend.Cpu, string? backendVersion = null,
        bool external = false, string? tag = null, int daysAgo = 0) =>
        new(Path.Combine(Path.GetTempPath(), "oc-engines", name),
            new EngineManifest
            {
                Tag = tag ?? "b" + build, Build = build, Backend = backend, BackendVersion = backendVersion,
                Arch = CpuArch.X64, InstalledAt = DateTime.UtcNow.AddDays(-daysAgo),
            }, external);

    [Fact]
    public void Finds_Newer_Release_With_Same_Backend()
    {
        var u = EngineUpdates.Find(Install("old", 12000), new[] { Release() });
        Assert.NotNull(u);
        Assert.Equal("b12176", u!.Tag);
        Assert.Equal(EngineBackend.Cpu, u.Variant.Backend);
        Assert.Equal(CpuArch.X64, u.Variant.Arch);
    }

    [Fact]
    public void Keeps_Cuda_Major_Version()
    {
        var u = EngineUpdates.Find(Install("cuda", 12000, EngineBackend.Cuda, "12.2"), new[] { Release() });
        Assert.NotNull(u);
        Assert.StartsWith("12.", u!.Variant.BackendVersion);
    }

    [Fact]
    public void No_Update_When_Current_Ignored_External_Or_Compat()
    {
        Assert.Null(EngineUpdates.Find(Install("cur", 12176), new[] { Release() }));
        Assert.Null(EngineUpdates.Find(Install("old", 12000), new[] { Release() }, ignoredTag: "b12176"));
        Assert.Null(EngineUpdates.Find(Install("mine", 12000, external: true), new[] { Release() }));
        // Сборка Win7-compat не «обновляется» на официальную (та может не запуститься на Win7)
        Assert.Null(EngineUpdates.Find(Install("w7", 9000, tag: "llama-b9000-win7"), new[] { Release() }));
    }

    [Fact]
    public void Cleanup_Keeps_Two_Latest_And_Active()
    {
        var a = Install("a", 100, daysAgo: 30);
        var b = Install("b", 200, daysAgo: 20);
        var c = Install("c", 300, daysAgo: 10);
        var d = Install("d", 400, daysAgo: 1);
        var ext = Install("ext", 50, external: true);
        var all = new[] { a, b, c, d, ext };

        Assert.Equal(new[] { "a", "b" }, EngineUpdates.CleanupCandidates(all, d.Id).Select(e => Path.GetFileName(e.Directory)).OrderBy(x => x));
        // Активна старая сборка — она остаётся вместе с двумя последними
        Assert.Equal(new[] { "b" }, EngineUpdates.CleanupCandidates(all, a.Id).Select(e => Path.GetFileName(e.Directory)));
        // Две скачанные — чистить нечего
        Assert.Empty(EngineUpdates.CleanupCandidates(new[] { c, d, ext }, d.Id));
    }

    [Theory]
    [InlineData("poulphunter/llama.cpp-windows7", "poulphunter/llama.cpp-windows7")]
    [InlineData("https://github.com/poulphunter/llama.cpp-windows7", "poulphunter/llama.cpp-windows7")]
    [InlineData("https://github.com/owner/repo/releases/tag/b1", "owner/repo")]
    [InlineData("github.com/owner/repo.git", "owner/repo")]
    [InlineData("not a repo", null)]
    [InlineData("", null)]
    public void Normalizes_Custom_Repo(string input, string? expected) => Assert.Equal(expected, EngineSources.NormalizeRepo(input));

    [Fact]
    public void Resolves_Build_Sources()
    {
        Assert.Equal((null, false), EngineSources.Resolve(EngineSource.Official, "x/y", "app/repo"));
        Assert.Equal(("app/repo", true), EngineSources.Resolve(EngineSource.AppWin7, null, "app/repo"));
        Assert.Equal(("x/y", false), EngineSources.Resolve(EngineSource.Custom, "https://github.com/x/y", "app/repo"));
    }
}