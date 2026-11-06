using System.IO;
using System.Linq;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void WritableExeDirectory_IsPortable()
    {
        using var tmp = new TempDir();
        var paths = AppPaths.Detect(tmp.Path, tmp.Combine("local"));

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.GetFullPath(tmp.Path), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "config", "settings.json"), paths.SettingsFile);
    }

    [Fact]
    public void MissingExeDirectory_FallsBackToLocalAppData()
    {
        using var tmp = new TempDir();
        var paths = AppPaths.Detect(tmp.Combine("does-not-exist"), tmp.Combine("local"));

        Assert.False(paths.IsPortable);
        Assert.Equal(tmp.Combine("local", AppPaths.AppFolderName), paths.Root);
    }

    [Fact]
    public void EnsureCreated_CreatesAllFolders()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Path, true);
        paths.EnsureCreated();

        foreach (var dir in new[] { paths.Engine, paths.Config, paths.Profiles, paths.Certs, paths.Models, paths.Chats, paths.Reports, paths.Logs, paths.Cache })
            Assert.True(Directory.Exists(dir), dir);
    }
}

public class JsonFileTests
{
    private sealed class Sample
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public SkinKind Skin { get; set; }
    }

    [Fact]
    public void WriteAtomic_ThenRead_RoundTrips_AndReplacesExisting()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("sub", "a.json");

        JsonFile.WriteAtomic(path, new Sample { Name = "one", Count = 1, Skin = SkinKind.Aero });
        JsonFile.WriteAtomic(path, new Sample { Name = "two", Count = 2, Skin = SkinKind.Fluent });

        var back = JsonFile.Read<Sample>(path)!;
        Assert.Equal("two", back.Name);
        Assert.Equal(2, back.Count);
        Assert.Equal(SkinKind.Fluent, back.Skin);
        Assert.Contains("\"Fluent\"", File.ReadAllText(path)); // enum пишется строкой
        Assert.Empty(Directory.GetFiles(tmp.Combine("sub"), "*.tmp"));
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(JsonFile.Read<Sample>(tmp.Combine("nope.json")));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_GivesDefaults()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Combine("settings.json"));
        var s = store.Load();

        Assert.Equal(SkinKind.Auto, s.Appearance.Skin);
        Assert.Equal(BackdropKind.Auto, s.Appearance.Backdrop);
        Assert.False(s.Logging.LogMessageContent);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Current.Appearance.Skin = SkinKind.Luna;
        store.Current.Appearance.Mode = ThemeMode.Dark;
        store.Current.Appearance.CustomAccent = "#ff8c00";
        store.Save();

        var again = new SettingsStore(path).Load();
        Assert.Equal(SkinKind.Luna, again.Appearance.Skin);
        Assert.Equal(ThemeMode.Dark, again.Appearance.Mode);
        Assert.Equal("#ff8c00", again.Appearance.CustomAccent);
    }

    [Fact]
    public void Load_CorruptFile_IsQuarantined_AndDefaultsUsed()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("settings.json");
        File.WriteAllText(path, "{ this is not json");

        var s = new SettingsStore(path).Load();

        Assert.Equal(SkinKind.Auto, s.Appearance.Skin);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(tmp.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Normalize_FixesNullsAndOutOfRangeValues()
    {
        var s = new AppSettings
        {
            Appearance = null!,
            Window = new WindowPlacementSettings { Width = 10, Height = double.NaN, SidebarWidth = 5000 },
            Logging = new LoggingSettings { MaxFiles = 0, MaxFileSizeMb = 9999, MinLevel = "bogus" },
            Language = "",
        };

        SettingsStore.Normalize(s);

        Assert.NotNull(s.Appearance);
        Assert.Equal("auto", s.Language);
        Assert.Equal(1180, s.Window.Width);
        Assert.Equal(760, s.Window.Height);
        Assert.Equal(260, s.Window.SidebarWidth);
        Assert.Equal(5, s.Logging.MaxFiles);
        Assert.Equal(5, s.Logging.MaxFileSizeMb);
        Assert.Equal("Info", s.Logging.MinLevel);
    }

    [Theory]
    [InlineData("#0078D4", true, 0x00, 0x78, 0xD4)]
    [InlineData("0078d4", true, 0x00, 0x78, 0xD4)]
    [InlineData("#abc", true, 0xAA, 0xBB, 0xCC)]
    [InlineData("#12345", false, 0, 0, 0)]
    [InlineData("#GGGGGG", false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    public void ColorHex_Parse(string text, bool ok, int r, int g, int b)
    {
        Assert.Equal(ok, ColorHex.TryParse(text, out var rr, out var gg, out var bb));
        if (ok) Assert.Equal((r, g, b), ((int)rr, (int)gg, (int)bb));
    }
}
