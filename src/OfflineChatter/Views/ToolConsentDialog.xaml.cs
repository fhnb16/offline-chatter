using System.Windows;
using Newtonsoft.Json.Linq;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.Views;

public enum ConsentAnswer
{
    Deny,
    Once,
    Always,
}

/// <summary>Подтверждение вызова инструмента: имя, источник, аргументы; «один раз», «всегда», «отклонить».</summary>
public partial class ToolConsentDialog : Window
{
    private ConsentAnswer _answer = ConsentAnswer.Deny;

    private ToolConsentDialog(string tool, string source, string arguments, bool isExternal)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Header.Text = L.F("Tools_ConsentHeader", tool);
        Source.Text = L.F("Tools_ConsentSource", source);
        try { Args.Text = JToken.Parse(arguments).ToString(Newtonsoft.Json.Formatting.Indented); }
        catch (Newtonsoft.Json.JsonException) { Args.Text = arguments; }
        if (isExternal)
        {
            Warning.Text = L.Get("Tools_ExternalWarning");
            Warning.Visibility = Visibility.Visible;
        }
    }

    public static ConsentAnswer Ask(string tool, string source, string arguments, bool isExternal)
    {
        var dlg = new ToolConsentDialog(tool, source, arguments, isExternal) { Owner = Application.Current?.MainWindow };
        dlg.ShowDialog();
        return dlg._answer;
    }

    private void OnDeny(object sender, RoutedEventArgs e) => Close();

    private void OnOnce(object sender, RoutedEventArgs e)
    {
        _answer = ConsentAnswer.Once;
        Close();
    }

    private void OnAlways(object sender, RoutedEventArgs e)
    {
        _answer = ConsentAnswer.Always;
        Close();
    }
}
