using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Engine;

/// <summary>Флаг llama-server из --help.</summary>
public sealed class EngineFlag
{
    public List<string> Names { get; } = new();
    public string? Argument { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Env { get; set; }
    public string Section { get; set; } = string.Empty;

    /// <summary>Основное имя: самое длинное "--xxx".</summary>
    public string Primary => Names.Where(n => n.StartsWith("--", StringComparison.Ordinal)).OrderByDescending(n => n.Length).FirstOrDefault() ?? Names.First();

    public bool IsRemoved => Description.IndexOf("has been removed", StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>
/// Каталог флагов конкретной версии llama-server (разбор --help). Флаги меняются от версии к версии,
/// поэтому UI показывает только то, что реально поддерживается.
/// Формат: имена с колонки 0, описание с колонки ~40, продолжение — с отступом, "(env: X)", секции "----- x -----".
/// </summary>
public sealed class FlagCatalog
{
    private static readonly Regex SectionRx = new(@"^-{3,}\s*(?<name>.+?)\s*-{3,}\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvRx = new(@"\(env:\s*(?<env>[A-Z0-9_]+)\)", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, EngineFlag> _byName = new(StringComparer.Ordinal);

    public FlagCatalog(IEnumerable<EngineFlag> flags)
    {
        Flags = flags.ToList();
        foreach (var f in Flags)
            foreach (var n in f.Names)
                _byName[n] = f;
    }

    public IReadOnlyList<EngineFlag> Flags { get; }

    public bool Supports(string name) => _byName.TryGetValue(name, out var f) && !f.IsRemoved;

    public EngineFlag? Find(string name) => _byName.TryGetValue(name, out var f) ? f : null;

    public static FlagCatalog Parse(string help)
    {
        var flags = new List<EngineFlag>();
        EngineFlag? current = null;
        var section = string.Empty;

        foreach (var raw in help.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var sm = SectionRx.Match(line);
            if (sm.Success)
            {
                section = sm.Groups["name"].Value;
                current = null;
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                current = ParseFlagLine(line);
                current.Section = section;
                flags.Add(current);
                continue;
            }

            if (current != null && char.IsWhiteSpace(raw[0]))
            {
                var text = line.Trim();
                var em = EnvRx.Match(text);
                if (em.Success)
                {
                    current.Env = em.Groups["env"].Value;
                    text = EnvRx.Replace(text, string.Empty).Trim();
                }
                if (text.Length > 0) current.Description = (current.Description + " " + text).Trim();
            }
        }
        return new FlagCatalog(flags);
    }

    private static EngineFlag ParseFlagLine(string line)
    {
        // Описание начинается после двух и более пробелов подряд (колонка ~40)
        var m = Regex.Match(line, @"\s{2,}");
        string spec, desc;
        if (m.Success && m.Index > 0)
        {
            // "-c,    --ctx-size N" — после запятой тоже бывает несколько пробелов; описание — после последнего разрыва в первой части
            var split = FindDescriptionStart(line);
            spec = line.Substring(0, split).Trim();
            desc = line.Substring(split).Trim();
        }
        else
        {
            spec = line.Trim();
            desc = string.Empty;
        }

        var flag = new EngineFlag { Description = desc };
        foreach (var part in spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (tokens[0].StartsWith("-", StringComparison.Ordinal)) flag.Names.Add(tokens[0]);
            if (tokens.Length > 1) flag.Argument = tokens[1].Trim();
        }
        return flag;
    }

    /// <summary>Позиция начала описания: первый разрыв из 2+ пробелов, после которого не идёт "-" (следующее имя).</summary>
    private static int FindDescriptionStart(string line)
    {
        foreach (Match gap in Regex.Matches(line, @"\s{2,}"))
        {
            var next = gap.Index + gap.Length;
            if (next < line.Length && line[next] != '-' && !(gap.Index > 0 && line[gap.Index - 1] == ','))
                return next;
        }
        return line.Length;
    }
}
