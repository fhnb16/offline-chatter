using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.Shell;

/// <summary>
/// Перенос программы из «Загрузок», с рабочего стола или из временной папки архиватора
/// в %LOCALAPPDATA%\Programs\OfflineChatter (как это делает установщик) с ярлыком в меню «Пуск».
/// </summary>
public static class Relocation
{
    public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    public static string Target => InstallLocation.DefaultTarget();

    public static bool IsInStandardLocation =>
        string.Equals(Path.GetFullPath(ExeDir).TrimEnd('\\'), Path.GetFullPath(Target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Первый запуск из неподходящей папки — спросить один раз (ответ «Нет» запоминается).</summary>
    public static async Task OfferIfNeededAsync(AppServices services)
    {
        var w = services.Settings.Current.Window;
        if (w.MoveOfferDismissed) return;
        if (!InstallLocation.ShouldOfferMove(ExeDir, Target, InstallLocation.DefaultTransientRoots())) return;

        var temp = InstallLocation.IsTemporary(ExeDir);
        var text = L.F(temp ? "Move_OfferTemp" : "Move_Offer", ExeDir, Target);
        var answer = MessageBox.Show(text, "Offline Chatter", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return; // спросим при следующем запуске
        if (answer == MessageBoxResult.No)
        {
            w.MoveOfferDismissed = true;
            services.Settings.Save();
            return;
        }
        await MoveAsync(services);
    }

    /// <summary>Скопировать программу с данными, создать ярлык, запустить новую копию и закрыть эту.</summary>
    public static async Task<bool> MoveAsync(AppServices services)
    {
        var exeName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
        string newExe;
        try
        {
            services.Engine.Server.Stop();
            services.Settings.Current.Window.MoveOfferDismissed = true;
            services.Settings.Save();
            Mouse.OverrideCursor = Cursors.Wait;
            newExe = await Task.Run(() => InstallLocation.CopyTo(ExeDir, Target, exeName));
            CreateStartMenuShortcut(newExe);
            Log.Info("App", "Program copied", new { from = ExeDir, to = Target });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("App", "Relocation failed", ex);
            MessageBox.Show(L.F("Move_Failed", ex.Message), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        MessageBox.Show(L.F("Move_Done", Target, ExeDir), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            Process.Start(new ProcessStartInfo(newExe) { UseShellExecute = false, WorkingDirectory = Target });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("App", "Cannot start relocated copy", ex);
            return false;
        }
        // Старая копия больше не нужна: её настройки уже перенесены, запись в старую папку не нужна
        services.Settings.Frozen = true;
        Application.Current.Shutdown();
        return true;
    }

    /// <summary>Ярлык в меню «Пуск» (WScript.Shell через позднее связывание — без ссылок на COM-сборки).</summary>
    public static void CreateStartMenuShortcut(string exe)
    {
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(programs)) return;
            var path = Path.Combine(programs, "Offline Chatter.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            var shell = Activator.CreateInstance(shellType);
            var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!;
            var t = link.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe)! });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Offline Chatter" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("App", "Cannot create Start menu shortcut", ex);
        }
    }
}
