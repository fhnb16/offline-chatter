using System;
using System.Drawing;
using System.Windows;
using OfflineChatter.Localization;
using Forms = System.Windows.Forms;

namespace OfflineChatter.Shell;

/// <summary>
/// Значок в области уведомлений (WinForms NotifyIcon — есть в .NET Framework, работает на Win7–11):
/// меню, всплывающие уведомления (balloon — на Win10/11 они показываются как системные toast).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action quickAsk, Action newChat, Action exit)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "Offline Chatter",
            Icon = LoadIcon(),
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.Get("Tray_Open"), null, (_, _) => open());
        menu.Items.Add(L.Get("Tray_QuickAsk") + (App.QuickAskHotkeyText.Length > 0 ? "\t" + App.QuickAskHotkeyText : string.Empty), null, (_, _) => quickAsk());
        menu.Items.Add(L.Get("Chat_New"), null, (_, _) => newChat());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.Get("Tray_Exit"), null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/OfflineChatter;component/Assets/app.ico"));
            if (info != null) return new Icon(info.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException) { }
        return SystemIcons.Application;
    }

    public void Notify(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
