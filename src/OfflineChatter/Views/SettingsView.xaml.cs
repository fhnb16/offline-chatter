using System.Windows;
using System.Windows.Controls;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnSetMasterPassword(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SetMasterPassword(MasterPwd.Password, MasterPwd2.Password);
        MasterPwd.Clear();
        MasterPwd2.Clear();
    }

    /// <summary>PasswordBox не поддерживает привязку — передаём пароль явно и сразу шифруем.</summary>
    private void OnProxyPasswordLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && ProxyPassword.Password.Length > 0)
        {
            vm.Network.SetProxyPassword(ProxyPassword.Password);
            ProxyPassword.Clear();
        }
    }
}
