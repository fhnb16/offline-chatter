using System;
using System.Globalization;
using System.Resources;
using System.Threading;
using System.Windows.Markup;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Localization;

/// <summary>
/// Строки интерфейса: Strings.resx (английский) и StringsRu.resx, оба генерируются из Strings.tsv (tools/gen-strings.ps1).
/// Язык выбирается при старте; смена языка вступает в силу после перезапуска.
/// </summary>
public static class L
{
    // Русские строки — отдельный ресурс в основной сборке (не satellite), чтобы exe оставался одним файлом.
    private static readonly ResourceManager En = new("OfflineChatter.Localization.Strings", typeof(L).Assembly);
    private static readonly ResourceManager Ru = new("OfflineChatter.Localization.StringsRu", typeof(L).Assembly);

    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <param name="language">"auto", "ru" или "en".</param>
    public static void Initialize(string language)
    {
        var name = language?.ToLowerInvariant() switch
        {
            "ru" => "ru-RU",
            "en" => "en-US",
            _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? "ru-RU" : "en-US",
        };
        Culture = CultureInfo.GetCultureInfo(name);
        Thread.CurrentThread.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static bool IsRussian => Culture.TwoLetterISOLanguageName == "ru";

    public static string Get(string key)
    {
        try
        {
            var s = (IsRussian ? Ru.GetString(key, CultureInfo.InvariantCulture) : null)
                    ?? En.GetString(key, CultureInfo.InvariantCulture);
            if (s != null) return s;
        }
        catch (MissingManifestResourceException ex)
        {
            Log.Error("L10n", "Strings resource missing", ex);
        }
        Log.Debug("L10n", "Missing string: " + key);
        return "[" + key + "]";
    }

    public static string F(string key, params object?[] args)
    {
        var fmt = Get(key);
        try
        {
            return string.Format(Culture, fmt, args);
        }
        catch (FormatException)
        {
            return fmt;
        }
    }
}

/// <summary>XAML: Text="{l:Loc Chat_New}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.Get(Key);
}
