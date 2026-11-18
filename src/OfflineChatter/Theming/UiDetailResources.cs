using System;
using System.Windows;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Theming;

/// <summary>
/// Вариант интерфейса (<see cref="UiDetail"/>) как ресурсы приложения: разметка ссылается на них через DynamicResource,
/// и все страницы перестраиваются сразу при смене уровня — без перезапуска и без привязок к модели представления.
/// <list type="bullet">
/// <item><c>UiHintVisibility</c> — пояснения под полями (скрыты у эксперта);</item>
/// <item><c>UiGuideVisibility</c> — пошаговые карточки «что сделать дальше» (только «Пошагово»);</item>
/// <item><c>UiAdvancedVisibility</c> — дополнительные параметры (скрыты в «Пошагово»);</item>
/// <item><c>UiExpertVisibility</c> — командная строка и сырые флаги (только «Эксперт»);</item>
/// <item><c>UiAdvancedExpanded</c> — раскрыты ли блоки «Дополнительно» сразу.</item>
/// </list>
/// </summary>
public static class UiDetailResources
{
    public static UiDetail Current { get; private set; } = UiDetail.Standard;

    public static event EventHandler? Changed;

    public static void Apply(UiDetail detail)
    {
        Current = detail;
        var r = Application.Current?.Resources;
        if (r == null) return;
        r["UiHintVisibility"] = detail == UiDetail.Expert ? Visibility.Collapsed : Visibility.Visible;
        r["UiGuideVisibility"] = detail == UiDetail.Guided ? Visibility.Visible : Visibility.Collapsed;
        r["UiAdvancedVisibility"] = detail == UiDetail.Guided ? Visibility.Collapsed : Visibility.Visible;
        r["UiExpertVisibility"] = detail == UiDetail.Expert ? Visibility.Visible : Visibility.Collapsed;
        r["UiAdvancedExpanded"] = detail == UiDetail.Expert;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
