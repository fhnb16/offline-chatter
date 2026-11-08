using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Engine;

/// <summary>Найденное обновление движка: новый релиз и вариант того же бэкенда и архитектуры.</summary>
public sealed record EngineUpdate(EngineInstall Current, EngineRelease Release, EngineVariant Variant)
{
    public string Tag => Release.Tag;
}

/// <summary>Обновления скачанных сборок llama.cpp и очистка старых версий.</summary>
public static class EngineUpdates
{
    /// <summary>Номер сборки установленного движка: из --version, иначе из тега.</summary>
    public static int BuildOf(EngineInstall e)
    {
        if (e.Manifest.Build is > 0) return e.Manifest.Build.Value;
        var m = Regex.Match(e.Manifest.Tag ?? string.Empty, @"b(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>Сборка из Win7-совместимого канала.</summary>
    public static bool IsCompatBuild(EngineInstall e) =>
        string.Equals(e.Manifest.SourceRepo, EngineSources.Win7Repo, StringComparison.OrdinalIgnoreCase)
        || (e.Manifest.Tag ?? string.Empty).StartsWith("llama-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Новейший релиз, у которого есть вариант того же бэкенда и архитектуры, что у активной сборки.
    /// Для CUDA — только та же основная версия CUDA (иначе может не подойти драйвер).
    /// Свои (подключённые) сборки не обновляются: их собирает сам пользователь.
    /// </summary>
    public static EngineUpdate? Find(EngineInstall? active, IEnumerable<EngineRelease> releases, string? ignoredTag = null)
    {
        if (active == null || active.IsExternal) return null;
        var current = BuildOf(active);
        var compat = IsCompatBuild(active);
        var versionTagged = string.Equals(active.Manifest.SourceRepo, EngineSources.Win7Repo, StringComparison.OrdinalIgnoreCase);
        if (versionTagged)
        {
            if (!TryParseVersionTag(active.Manifest.Tag, out var currentVersion)) return null;
            var versionedReleases = releases
                .Where(r => TryParseVersionTag(r.Tag, out var version) && version > currentVersion)
                .OrderByDescending(r => ParseVersionTag(r.Tag))
                .ToList();
            return FindMatching(active, versionedReleases, ignoredTag, versionTagged: true);
        }

        if (current <= 0) return null;
        var buildReleases = releases.Where(r => r.BuildNumber > current).OrderByDescending(r => r.BuildNumber);
        return FindMatching(active, buildReleases, ignoredTag, versionTagged: false);
    }

    private static EngineUpdate? FindMatching(EngineInstall active, IEnumerable<EngineRelease> releases, string? ignoredTag, bool versionTagged)
    {
        var compat = IsCompatBuild(active);
        foreach (var r in releases)
        {
            if (string.Equals(r.Tag, ignoredTag, StringComparison.OrdinalIgnoreCase)) return null; // пропущенная версия и всё, что старше неё
            if (!versionTagged && compat != r.Tag.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)) continue;
            var variant = PickVariant(active.Manifest, AssetMatcher.WindowsVariants(r));
            if (variant != null) return new EngineUpdate(active, r, variant);
        }
        return null;
    }

    private static Version ParseVersionTag(string tag) => Version.Parse(tag.Substring(1));

    private static bool TryParseVersionTag(string tag, out Version version)
    {
        version = new Version();
        return tag.Length > 1
            && (tag[0] == 'v' || tag[0] == 'V')
            && Version.TryParse(tag.Substring(1), out version);
    }

    internal static EngineVariant? PickVariant(EngineManifest m, IReadOnlyList<EngineVariant> variants)
    {
        var same = variants.Where(v => v.Backend == m.Backend && v.Arch == m.Arch).ToList();
        if (same.Count == 0) return null;
        var exact = same.FirstOrDefault(v => string.Equals(v.BackendVersion, m.BackendVersion, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        if (m.Backend != EngineBackend.Cuda) return same[0];
        var major = Major(m.BackendVersion);
        return same.Where(v => Major(v.BackendVersion) == major && major != null)
                   .OrderByDescending(v => Version.TryParse(v.BackendVersion, out var x) ? x : new Version())
                   .FirstOrDefault();
    }

    private static string? Major(string? version) => version?.Split('.')[0];

    /// <summary>
    /// Что удалить при очистке: из скачанных сборок (свои папки не трогаем) оставляем две последние
    /// (по номеру сборки, затем по дате установки) и всегда — активную. Пока скачанных меньше трёх — нечего чистить.
    /// </summary>
    public static IReadOnlyList<EngineInstall> CleanupCandidates(IEnumerable<EngineInstall> installed, string? activeId, int keep = 2)
    {
        var local = installed.Where(e => !e.IsExternal)
            .OrderByDescending(BuildOf)
            .ThenByDescending(e => e.Manifest.InstalledAt)
            .ToList();
        if (local.Count <= keep) return Array.Empty<EngineInstall>();
        var kept = new HashSet<string>(local.Take(keep).Select(e => e.Id));
        if (activeId != null) kept.Add(activeId.ToLowerInvariant());
        return local.Where(e => !kept.Contains(e.Id)).ToList();
    }
}
