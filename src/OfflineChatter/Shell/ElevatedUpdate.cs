using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Net;
using OfflineChatter.Localization;

namespace OfflineChatter.Shell;

/// <summary>
/// Обновление программы, установленной туда, куда без прав администратора писать нельзя (Program Files).
/// Обычная копия скачивает и проверяет новую версию, затем запускает саму себя с правами администратора
/// и ключом <c>--apply-update</c> и закрывается. Повышенный процесс ждёт её завершения, подменяет exe,
/// запускает обновлённую программу снова от имени обычного пользователя (через explorer.exe) и выходит без окна.
/// </summary>
public static class ElevatedUpdate
{
    public const string Switch = "--apply-update";
    private const int ErrorCancelled = 1223; // пользователь нажал «Нет» в окне UAC

    /// <summary>Запустить повышенный процесс. false — пользователь отказался в UAC или запуск не удался.</summary>
    public static bool TryStart(string targetExe, string newExe, string? newConfig)
    {
        var args = $"{Switch} \"{newExe}\" \"{newConfig ?? "-"}\" {Process.GetCurrentProcess().Id}";
        try
        {
            Process.Start(new ProcessStartInfo(targetExe, args) { Verb = "runas", UseShellExecute = true });
            Log.Info("Update", "Elevated updater started");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Update", "Elevation cancelled by user");
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Update", "Cannot start elevated updater", ex);
            return false;
        }
    }

    /// <summary>Режим повышенного процесса: <c>--apply-update новый.exe новый.config|- pid</c>. Возвращает код выхода.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 4) return 2;
        var newExe = args[1];
        var newConfig = args[2] == "-" ? null : args[2];
        var target = App.ExePath;

        // Ждём, пока обычная копия программы закроется (иначе её exe.config может быть занят)
        if (int.TryParse(args[3], out var pid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.WaitForExit(30000);
            }
            catch (ArgumentException) { /* уже завершилась */ }
        }

        try
        {
            UpdateChecker.Swap(target, newExe, newConfig);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(L.F("Update_Failed", ex.Message), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        // Запуск обновлённой программы без прав администратора: через проводник, а не дочерним процессом
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(L.Get("Update_DoneStartManually"), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Старый exe — это файл, из которого запущен этот процесс: удалить его можно только после выхода.
        // Обычный пользователь в Program Files удалить его не сможет, поэтому удаляем отсюда, с паузой.
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & del /f /q \"{target}.old\" \"{target}.config.old\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return 0;
    }
}
