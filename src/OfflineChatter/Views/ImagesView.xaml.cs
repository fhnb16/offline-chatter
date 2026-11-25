using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

/// <summary>Страница генерации: перетаскивание картинок, Ctrl+V с изображением, открытие просмотра (чисто визуальная логика).</summary>
public partial class ImagesView : UserControl
{
    public ImagesView() => InitializeComponent();

    private ImagesViewModel? Vm => DataContext as ImagesViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _ = Vm.AddFilesAsync(files.Where(f => f.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", System.StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".bmp", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase)));
        e.Handled = true;
    }

    /// <summary>
    /// Ctrl+Enter запускает генерацию (поле промпта само съело бы Enter как перевод строки);
    /// Ctrl+V с картинкой в буфере добавляет референс, текст вставляется в поле как обычно.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Enter)
        {
            if (Vm.GenerateCommand.CanExecute(null)) Vm.GenerateCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.V) e.Handled = Vm.TryPasteImage(allowFilesWithText: false);
    }

    private void OnGalleryDoubleClick(object sender, MouseButtonEventArgs e) => Vm?.OpenImageCommand.Execute(Vm.SelectedImage);

    private void OnPreviewClick(object sender, MouseButtonEventArgs e) => Vm?.OpenImageCommand.Execute(Vm.SelectedImage);
}
