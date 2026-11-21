using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
    public static bool TryStart(string targetExe, string newExe, string? newConfig, UpdateFileHashes approved, bool consented)
    {
        var args = $"{Switch} \"{newExe}\" \"{newConfig ?? "-"}\" {Process.GetCurrentProcess().Id} {approved.Executable} {approved.Configuration ?? "-"} {consented}";
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

    /// <summary>Режим повышенного процесса. Возвращает код выхода.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 7 || !int.TryParse(args[3], out var pid) || pid <= 0 || !bool.TryParse(args[6], out var consented))
            return 2;
        var newExe = args[1];
        var newConfig = args[2] == "-" ? null : args[2];
        var approved = new UpdateFileHashes(args[4], args[5] == "-" ? null : args[5]);
        var target = App.ExePath;

        // Ждём, пока обычная копия программы закроется (иначе её exe.config может быть занят)
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(30000))
            {
                MessageBox.Show(L.Get("Update_ExitTimedOut"), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        catch (ArgumentException) { /* уже завершилась */ }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(L.F("Update_Failed", ex.Message), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        UpdateInstallation installation;
        try
        {
            using var candidate = File.OpenRead(newExe);
            if (!approved.Matches(UpdateFileHashes.Read(newExe, newConfig, CancellationToken.None)))
            {
                Log.Warn("Update", "Update files changed while waiting for elevation");
                MessageBox.Show(L.Get("Update_FileChanged"), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
                return 3;
            }
            var (verdict, reason, _) = Authenticode.CheckUpdate(target, newExe);
            if (verdict == UpdateSignatureVerdict.Reject)
            {
                MessageBox.Show(L.Get("Update_SignatureRejected_" + reason), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
                return 3;
            }
            if (verdict == UpdateSignatureVerdict.Ask && !consented)
            {
                MessageBox.Show(L.Get("Update_TrustChanged"), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 3;
            }
            installation = UpdateChecker.Swap(target, newExe, newConfig, approved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(L.F("Update_Failed", ex.Message), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        // Запуск обновлённой программы без прав администратора: через проводник, а не дочерним процессом
        try
        {
            using var launcher = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false })
                ?? throw new InvalidOperationException(L.Get("Update_RestartFailed"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var detail = ex.Message;
            try { installation.Rollback(); }
            catch (IOException rollback)
            {
                detail += Environment.NewLine + rollback.Message;
            }
            MessageBox.Show(L.F("Update_Failed", detail), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        return 0;
    }
}
