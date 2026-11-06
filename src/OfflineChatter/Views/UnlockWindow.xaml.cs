using System.Windows;
using OfflineChatter.Core.Storage;
using OfflineChatter.Theming;

namespace OfflineChatter.Views;

/// <summary>Ввод мастер-пароля при запуске. «Пропустить» — работать без сохранённых ключей (их можно ввести заново).</summary>
public partial class UnlockWindow : Window
{
    public UnlockWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Loaded += (_, _) => Password.Focus();
    }

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (Secrets.Unlock(Password.Password))
        {
            DialogResult = true;
            return;
        }
        Error.Visibility = Visibility.Visible;
        Password.Clear();
        Password.Focus();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => DialogResult = false;
}
