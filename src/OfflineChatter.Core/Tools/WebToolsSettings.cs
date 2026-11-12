using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineChatter.Core.Tools;

/// <summary>Кем представляться сайтам: многие отдают «неизвестным программам» заглушку или 403.</summary>
public enum UserAgentPreset
{
    /// <summary>Честное имя программы: OfflineChatter/версия.</summary>
    App,
    ChromeWindows,
    EdgeWindows,
    FirefoxWindows,
    SafariMac,
    /// <summary>Мобильные версии страниц обычно легче и чище.</summary>
    ChromeAndroid,
    SafariIphone,
    Custom,
}

/// <summary>Поисковая система: готовый шаблон адреса выдачи, свой экземпляр SearXNG или свой адрес.</summary>
public enum SearchEngine
{
    /// <summary>HTML-выдача DuckDuckGo: без ключа, без скриптов и без своего сервера.</summary>
    DuckDuckGo,
    Bing,
    Google,
    Yandex,
    Brave,
    Mojeek,
    Startpage,
    Yahoo,
    /// <summary>Свой экземпляр SearXNG (JSON API).</summary>
    Searxng,
    /// <summary>Свой адрес выдачи: <see cref="WebToolsSettings.CustomSearchUrl"/>.</summary>
    Custom,
}

public enum SafeSearchLevel
{
    Off,
    Moderate,
    Strict,
}

/// <summary>Настройки web_fetch и web_search: запросы, очистка страниц, поиск. Всё отключаемо.</summary>
public sealed class WebToolsSettings
{
    // ---- Запросы (общие для обоих инструментов) ----

    public UserAgentPreset UserAgent { get; set; } = UserAgentPreset.ChromeWindows;

    /// <summary>Строка User-Agent для <see cref="UserAgentPreset.Custom"/>.</summary>
    public string? CustomUserAgent { get; set; }

    /// <summary>Заголовок Accept-Language, например "ru-RU,ru;q=0.9,en;q=0.5" (пусто — не отправляется).</summary>
    public string? AcceptLanguage { get; set; }

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Сколько скачивать с одной страницы, КБ: остальное отбрасывается.</summary>
    public int MaxDownloadKb { get; set; } = 2048;

    /// <summary>Разрешить модели адреса этого компьютера и локальной сети (localhost, 192.168.x.x…).</summary>
    public bool AllowLocalAddresses { get; set; }

    // ---- Очистка страниц ----

    /// <summary>Оставлять только основной блок страницы (main / article / самый «текстовый» контейнер).</summary>
    public bool MainContentOnly { get; set; } = true;

    /// <summary>Убирать меню, шапку, подвал, боковые колонки, «хлебные крошки».</summary>
    public bool StripNavigation { get; set; } = true;

    /// <summary>Убирать рекламу, баннеры cookie, подписки, «поделиться», похожие материалы, скрытые блоки и поля форм.</summary>
    public bool StripBoilerplate { get; set; } = true;

    /// <summary>Убирать блоки комментариев.</summary>
    public bool StripComments { get; set; } = true;

    /// <summary>Убирать строки, которые уже встречались выше.</summary>
    public bool DropDuplicateLines { get; set; } = true;

    /// <summary>Оставлять ссылки в виде [текст](адрес).</summary>
    public bool KeepLinks { get; set; }

    /// <summary>Размечать заголовки, списки, цитаты и код как Markdown.</summary>
    public bool Markdown { get; set; } = true;

    /// <summary>Сколько символов текста страницы отдавать модели за один вызов.</summary>
    public int MaxChars { get; set; } = 12000;

    // ---- Поиск ----

    public SearchEngine Engine { get; set; } = SearchEngine.DuckDuckGo;

    /// <summary>
    /// Адрес выдачи для <see cref="SearchEngine.Custom"/>: запрос подставляется вместо {query} (или %s),
    /// а если метки нет — дописывается в конец, например https://html.duckduckgo.com/html/?q=
    /// </summary>
    public string? CustomSearchUrl { get; set; }

    public int MaxResults { get; set; } = 6;

    /// <summary>Регион выдачи в виде «страна-язык»: "ru-ru", "us-en"… (пусто — без региона).</summary>
    public string? Region { get; set; }

    public SafeSearchLevel SafeSearch { get; set; } = SafeSearchLevel.Moderate;

    /// <summary>Добавлять к результатам фрагменты текста из выдачи.</summary>
    public bool Snippets { get; set; } = true;

    /// <summary>Сколько первых страниц выдачи сразу скачать и приложить их текст (0 — только ссылки).</summary>
    public int ReadPages { get; set; }

    /// <summary>Сколько символов брать с каждой такой страницы.</summary>
    public int ReadPageChars { get; set; } = 3000;

    /// <summary>Сайты, которые не показывать в результатах ("pinterest.com" закрывает и поддомены).</summary>
    public List<string> ExcludeDomains { get; set; } = new();

    public PageCleanOptions CleanOptions => new()
    {
        MainContentOnly = MainContentOnly,
        StripNavigation = StripNavigation,
        StripBoilerplate = StripBoilerplate,
        StripComments = StripComments,
        DropDuplicateLines = DropDuplicateLines,
        KeepLinks = KeepLinks,
        Markdown = Markdown,
    };

    /// <summary>Возвращает значения в допустимые пределы после ручной правки файла.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(typeof(UserAgentPreset), UserAgent)) UserAgent = UserAgentPreset.ChromeWindows;
        if (!Enum.IsDefined(typeof(SearchEngine), Engine)) Engine = SearchEngine.DuckDuckGo;
        if (!Enum.IsDefined(typeof(SafeSearchLevel), SafeSearch)) SafeSearch = SafeSearchLevel.Moderate;
        if (TimeoutSeconds is < 3 or > 120) TimeoutSeconds = 20;
        if (MaxDownloadKb is < 64 or > 16384) MaxDownloadKb = 2048;
        if (MaxChars is < 1000 or > 60000) MaxChars = 12000;
        if (MaxResults is < 1 or > 20) MaxResults = 6;
        if (ReadPages is < 0 or > 3) ReadPages = 0;
        if (ReadPageChars is < 500 or > 10000) ReadPageChars = 3000;
        ExcludeDomains = ParseDomains(string.Join(",", ExcludeDomains ?? new List<string>()));
    }

    /// <summary>Список сайтов из строки: через запятую, пробел или с новой строки; адреса сводятся к имени хоста.</summary>
    public static List<string> ParseDomains(string? text)
    {
        var list = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = raw.Trim().ToLowerInvariant();
            var scheme = d.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) d = d.Substring(scheme + 3);
            var slash = d.IndexOf('/');
            if (slash >= 0) d = d.Substring(0, slash);
            if (d.StartsWith("www.", StringComparison.Ordinal)) d = d.Substring(4);
            d = d.Trim('.');
            if (d.Length > 0 && !list.Contains(d)) list.Add(d);
        }
        return list;
    }
}

/// <summary>Шаблоны User-Agent. Версии браузеров собраны здесь, чтобы обновлять их в одном месте.</summary>
public static class UserAgents
{
    private const string Chrome = "142.0.0.0";
    private const string Firefox = "140.0";
    private const string Safari = "18.5";

    public static IReadOnlyList<UserAgentPreset> Presets { get; } =
        Enum.GetValues(typeof(UserAgentPreset)).Cast<UserAgentPreset>().ToList();

    /// <summary>Строка заголовка для шаблона. <paramref name="appUserAgent"/> — имя программы (шаблон App и запасной вариант).</summary>
    public static string Resolve(UserAgentPreset preset, string? custom, string appUserAgent) => preset switch
    {
        UserAgentPreset.ChromeWindows => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{Chrome} Safari/537.36",
        UserAgentPreset.EdgeWindows => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{Chrome} Safari/537.36 Edg/{Chrome}",
        UserAgentPreset.FirefoxWindows => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{Firefox}) Gecko/20100101 Firefox/{Firefox}",
        UserAgentPreset.SafariMac => $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{Safari} Safari/605.1.15",
        UserAgentPreset.ChromeAndroid => $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{Chrome} Mobile Safari/537.36",
        UserAgentPreset.SafariIphone => $"Mozilla/5.0 (iPhone; CPU iPhone OS {Safari.Replace('.', '_')} like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{Safari} Mobile/15E148 Safari/604.1",
        UserAgentPreset.Custom when !string.IsNullOrWhiteSpace(custom) => Sanitize(custom!),
        _ => appUserAgent,
    };

    /// <summary>Заголовок — одна строка: переводы строк из вставленного текста не должны попасть в запрос.</summary>
    private static string Sanitize(string s) => new string(s.Where(c => c >= ' ' && c != 127).ToArray()).Trim();
}
