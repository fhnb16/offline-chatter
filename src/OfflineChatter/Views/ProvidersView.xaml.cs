using System.Windows;
using System.Windows.Controls;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

public partial class ProvidersView : UserControl
{
    public ProvidersView() => InitializeComponent();

    /// <summary>Ключ API из PasswordBox: сразу шифруется (DPAPI) и поле очищается.</summary>
    private void OnKeyLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.Tag is ProviderItemViewModel item && box.Password.Length > 0)
        {
            item.SetKey(box.Password);
            box.Clear();
        }
    }
}
