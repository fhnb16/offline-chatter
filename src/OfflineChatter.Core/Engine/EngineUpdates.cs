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
    /// <summary>Номер сборки установленного движка: из --version, иначе из тега ("b12176", "llama-b9000-win7").</summary>
    public static int BuildOf(EngineInstall e)
    {
        if (e.Manifest.Build is > 0) return e.Manifest.Build.Value;
        var m = Regex.Match(e.Manifest.Tag ?? string.Empty, @"b(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>Сборка из канала Win7-compat (релизы llama-* из репозитория программы).</summary>
    public static bool IsCompatBuild(EngineInstall e) => (e.Manifest.Tag ?? string.Empty).StartsWith("llama-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Новейший релиз, у которого есть вариант того же бэкенда и архитектуры, что у активной сборки.
    /// Для CUDA — только та же основная версия CUDA (иначе может не подойти драйвер).
    /// Свои (подключённые) сборки не обновляются: их собирает сам пользователь.
    /// </summary>
    public static EngineUpdate? Find(EngineInstall? active, IEnumerable<EngineRelease> releases, string? ignoredTag = null)
    {
        if (active == null || active.IsExternal) return null;
        var current = BuildOf(active);
        if (current <= 0) return null;
        var compat = IsCompatBuild(active);

        foreach (var r in releases.Where(r => r.BuildNumber > current).OrderByDescending(r => r.BuildNumber))
        {
            if (string.Equals(r.Tag, ignoredTag, StringComparison.OrdinalIgnoreCase)) return null; // пропущенная версия и всё, что старше неё
            if (compat != r.Tag.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)) continue;
            var variant = PickVariant(active.Manifest, AssetMatcher.WindowsVariants(r));
            if (variant != null) return new EngineUpdate(active, r, variant);
        }
        return null;
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
