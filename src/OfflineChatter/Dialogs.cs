using System.IO;

namespace OfflineChatter;

/// <summary>Системные диалоги, которых нет в WPF.</summary>
public static class Dialogs
{
    /// <summary>Выбор папки (системный диалог WinForms: есть в .NET Framework, вес не растёт).</summary>
    public static string? PickFolder(string title, string? initial = null)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            ShowNewFolderButton = true,
            SelectedPath = initial != null && Directory.Exists(initial) ? initial : string.Empty,
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }
}
