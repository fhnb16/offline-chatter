using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Mcp;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Core.Tools;
using Xunit;

namespace OfflineChatter.Core.Tests;

public sealed class DataTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oc-transfer-" + Guid.NewGuid().ToString("N"));

    public DataTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AppPaths NewPaths(string name)
    {
        var p = new AppPaths(Path.Combine(_dir, name), true);
        p.EnsureCreated();
        return p;
    }

    [Fact]
    public void Backup_Roundtrip_Restores_Config_And_Chats()
    {
        var a = NewPaths("a");
        File.WriteAllText(a.SettingsFile, "{\"Language\":\"ru\"}");
        File.WriteAllText(a.PromptsFile, "[]");
        File.WriteAllText(Path.Combine(a.Config, "settings.json.1234.tmp"), "junk");
        File.WriteAllText(Path.Combine(a.Chats, "c1.json"), "{}");
        var zip = Path.Combine(_dir, "backup.zip");

        var info = Backup.Create(a, zip, includeChats: true, "1.0.0");
        Assert.Equal(2, info.ConfigFiles); // .tmp пропущен
        Assert.Equal(1, info.ChatFiles);
        Assert.Equal(2, Backup.Inspect(zip).ConfigFiles);

        var b = NewPaths("b");
        File.WriteAllText(b.SettingsFile, "{\"Language\":\"en\"}");
        var safety = Backup.Restore(b, zip, restoreChats: true, "1.0.0");

        Assert.Contains("ru", File.ReadAllText(b.SettingsFile));
        Assert.True(File.Exists(Path.Combine(b.Chats, "c1.json")));
        Assert.True(File.Exists(safety));
        Assert.Single(Backup.ListSafetyCopies(b));
    }

    [Fact]
    public void Backup_Rejects_Foreign_Zip_And_Blocks_ZipSlip()
    {
        var foreign = Path.Combine(_dir, "foreign.zip");
        using (var z = ZipFile.Open(foreign, ZipArchiveMode.Create)) z.CreateEntry("x.txt");
        Assert.Throws<InvalidDataException>(() => Backup.Inspect(foreign));

        var evil = Path.Combine(_dir, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("backup.json").Open())) w.Write("{\"format\":\"offline-chatter-backup\"}");
            using (var w = new StreamWriter(z.CreateEntry("config/../../escaped.txt").Open())) w.Write("x");
        }
        var p = NewPaths("c");
        Backup.Restore(p, evil, false, "1");
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public void Chats_Export_Import_Roundtrip_With_New_Ids()
    {
        var chat = new ChatSession { Title = "T", SystemPrompt = "sys" };
        var u = chat.Append(new ChatMessage { Role = ChatRole.User, Content = "hi" });
        chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "hello", Stats = new GenerationStats { CompletionTokens = 3 } });
        var file = Path.Combine(_dir, "chats.json");
        ChatTransfer.Export(new[] { chat }, file);

        var existing = new HashSet<string> { chat.Id };
        var imported = ChatTransfer.Import(file, existing).Single();
        Assert.NotEqual(chat.Id, imported.Id); // такой id уже есть — получаем копию
        Assert.Equal("T", imported.Title);
        Assert.Equal("sys", imported.SystemPrompt);
        Assert.Equal(new[] { "hi", "hello" }, imported.ActivePath().Select(m => m.Content));
        Assert.Equal(u.Id, imported.Messages[0].Id);
        Assert.Equal(3, imported.Messages[1].Stats!.CompletionTokens);
    }

    [Fact]
    public void Imports_Plain_Message_Lists()
    {
        var file = Path.Combine(_dir, "plain.json");
        File.WriteAllText(file, "[{\"role\":\"system\",\"content\":\"be brief\"},{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"2+2?\"}]},{\"role\":\"assistant\",\"content\":\"4\"}]");
        var chat = ChatTransfer.Import(file, new HashSet<string>()).Single();
        Assert.Equal("be brief", chat.SystemPrompt);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, chat.ActivePath().Select(m => m.Role));
        Assert.Equal("2+2?", chat.ActivePath()[0].Content);
        Assert.Equal("plain", chat.Title);
    }

    [Fact]
    public void Prompt_Presets_Seed_And_Restore()
    {
        var file = Path.Combine(_dir, "prompts.json");
        var store = new PromptPresetStore(file);
        store.SeedIfEmpty(russianNames: true);
        Assert.Contains(store.Items, p => p.Name == "Программист");
        store.Items.RemoveAll(p => p.Id == "coder");
        store.Items.Add(new PromptPreset { Name = "Mine", Text = "x" });
        store.Save();

        var reloaded = new PromptPresetStore(file);
        reloaded.SeedIfEmpty(true); // файл есть — не трогаем
        Assert.DoesNotContain(reloaded.Items, p => p.Id == "coder");
        Assert.Equal(1, reloaded.RestoreDefaults(false));
        Assert.Contains(reloaded.Items, p => p.Name == "Mine");
    }

    [Fact]
    public void Builtin_Mcp_Is_Seeded_Once()
    {
        var file = Path.Combine(_dir, "mcp.json");
        var settings = new ToolsSettings();
        var store = new McpConfigStore(file);
        Assert.True(store.SeedBuiltin(settings));
        Assert.True(McpConfigStore.IsBuiltin(store.Servers.Single()));
        Assert.Equal("http", store.Servers[0].Transport);

        store.Servers.Clear();
        store.Save();
        Assert.False(store.SeedBuiltin(settings)); // пользователь удалил — не возвращаем
        Assert.Empty(new McpConfigStore(file).Servers);
    }

    [Fact]
    public void Update_Picks_App_Release_Not_Llama_Builds()
    {
        var releases = JArray.Parse(@"[
          {""tag_name"":""llama-b9000-win7"",""draft"":false,""assets"":[]},
          {""tag_name"":""v0.3.0"",""draft"":true,""assets"":[]},
          {""tag_name"":""v0.2.5"",""draft"":false,""html_url"":""https://x/rel"",""assets"":[
             {""name"":""OfflineChatter.exe"",""size"":10,""browser_download_url"":""https://x/OfflineChatter.exe"",""digest"":""sha256:abcd""},
             {""name"":""OfflineChatter.exe.config"",""size"":1,""browser_download_url"":""https://x/OfflineChatter.exe.config""}]},
          {""tag_name"":""v0.1.0"",""draft"":false,""assets"":[]}
        ]");
        var info = UpdateChecker.Pick(releases, "fhnb16/offline-chatter", new Version(0, 2, 0));
        Assert.True(info.Available);
        Assert.Equal("v0.2.5", info.LatestTag);
        Assert.True(info.CanInstall);
        Assert.Equal("abcd", info.Exe!.Sha256);
        Assert.NotNull(info.Config);

        Assert.False(UpdateChecker.Pick(releases, "r", new Version(0, 2, 5)).Available);
    }

    [Fact]
    public void Update_Prefers_Zip_Package_And_Extracts_Exe()
    {
        var releases = JArray.Parse(@"[
          {""tag_name"":""v0.3.1"",""draft"":false,""assets"":[
             {""name"":""OfflineChatter-0.3.1-Setup.exe"",""size"":10,""browser_download_url"":""https://x/s.exe"",""digest"":""sha256:11""},
             {""name"":""OfflineChatter-0.3.1.rar"",""size"":10,""browser_download_url"":""https://x/a.rar"",""digest"":""sha256:22""},
             {""name"":""OfflineChatter-0.3.1.zip"",""size"":10,""browser_download_url"":""https://x/a.zip"",""digest"":""sha256:33""}]}
        ]");
        var info = UpdateChecker.Pick(releases, "r", new Version(0, 3, 0));
        Assert.True(info.CanInstall);
        Assert.Null(info.Exe);
        Assert.Equal("OfflineChatter-0.3.1.zip", info.Package!.Name);

        var zip = Path.Combine(_dir, "pkg.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("OfflineChatter/OfflineChatter.exe").Open())) w.Write("new-exe");
            using (var w = new StreamWriter(z.CreateEntry("OfflineChatter/OfflineChatter.exe.config").Open())) w.Write("cfg");
            using (var w = new StreamWriter(z.CreateEntry("OfflineChatter/models/README.txt").Open())) w.Write("put models here");
        }
        var (exe, config) = UpdateChecker.ExtractPackage(zip, Path.Combine(_dir, "x"));
        Assert.Equal("new-exe", File.ReadAllText(exe));
        Assert.Equal("cfg", File.ReadAllText(config!));
    }

    [Fact]
    public void Update_Swap_Replaces_Exe_And_Keeps_Old()
    {
        var exe = Path.Combine(_dir, "app.exe");
        var fresh = Path.Combine(_dir, "new.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".config", "oldcfg");
        File.WriteAllText(fresh, "new");
        File.WriteAllText(fresh + ".config", "newcfg");

        // Работающий exe открыт без FILE_SHARE_DELETE, но переименование разрешено — имитируем открытым дескриптором
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            UpdateChecker.Swap(exe, fresh, fresh + ".config");

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.Equal("newcfg", File.ReadAllText(exe + ".config"));
        UpdateChecker.CleanupOld(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void Settings_Default_Update_Repo_And_Hidden_Pages()
    {
        var s = new AppSettings { UpdateRepo = "" };
        s.Window.HiddenPages = null!;
        s.Attachments = null!;
        SettingsStore.Normalize(s);
        Assert.Equal(AppSettings.DefaultUpdateRepo, s.UpdateRepo);
        Assert.NotNull(s.Window.HiddenPages);
        Assert.Equal(1536, s.Attachments.ImageMaxSide);
    }
}
