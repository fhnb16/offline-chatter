using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace OfflineChatter.Views;

/// <summary>Группы строк «подпись — значение»: сведения о сервере, подключении, токенах.</summary>
public partial class ServerInsightView : UserControl
{
    /// <summary>Группы (InsightGroup), которые нужно показать: все — в «Движке», выбранные — в правой панели чата.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(IEnumerable), typeof(ServerInsightView), new PropertyMetadata(null));

    public IEnumerable? Source
    {
        get => (IEnumerable?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ServerInsightView() => InitializeComponent();
}
