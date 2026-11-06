using System.Windows;
using System.Windows.Controls;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

public partial class ModelsView : UserControl
{
    public ModelsView() => InitializeComponent();

    /// <summary>PasswordBox не поддерживает привязку — токен передаётся явно и сразу шифруется.</summary>
    private void OnHfTokenLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModelsViewModel vm && HfToken.Password.Length > 0)
        {
            vm.SetHfToken(HfToken.Password);
            HfToken.Clear();
        }
    }
}
