using System;
using System.IO;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class ExternalThemeToolsTests
{
    // Сокращённый реальный settings.json Mica For Everyone 2.0.6
    private const string Settings =
        "{\"rules\":[{\"type\":\"global\",\"priority\":0,\"titleBarColor\":\"System\",\"backdropPreference\":\"MicaAlt\",\"cornerPreference\":\"Default\",\"extendFrameIntoClientArea\":false,\"enableBlurBehind\":false,\"titleBarColorCode\":null}," +
        "{\"type\":\"process\",\"processName\":\"notepad\",\"priority\":1,\"titleBarColor\":\"Default\",\"backdropPreference\":\"Default\",\"cornerPreference\":\"Default\",\"extendFrameIntoClientArea\":false,\"enableBlurBehind\":false,\"titleBarColorCode\":null}]," +
        "\"telemetryEnabled\":false}";

    [Fact]
    public void Adds_Default_Rule_Once_And_Keeps_Others()
    {
        Assert.False(ExternalThemeTools.HasProcessRule(Settings, "OfflineChatter"));
        var updated = ExternalThemeTools.AddExclusion(Settings, "OfflineChatter");
        Assert.True(ExternalThemeTools.HasProcessRule(updated, "offlinechatter"));

        var root = JObject.Parse(updated);
        Assert.Equal(3, ((JArray)root["rules"]!).Count);
        Assert.False((bool)root["telemetryEnabled"]!);
        var rule = (JObject)root["rules"]![2]!;
        Assert.Equal("Default", (string?)rule["titleBarColor"]);
        Assert.Equal("Default", (string?)rule["backdropPreference"]);

        Assert.Equal(updated, ExternalThemeTools.AddExclusion(updated, "OfflineChatter"));
    }

    [Fact]
    public void File_Update_Makes_Backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oc-mfe-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "LocalState", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Settings);
        try
        {
            Assert.True(ExternalThemeTools.AddExclusionToFile(file, "OfflineChatter"));
            Assert.Equal(Settings, File.ReadAllText(file + ".bak"));
            Assert.False(ExternalThemeTools.AddExclusionToFile(file, "OfflineChatter"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
