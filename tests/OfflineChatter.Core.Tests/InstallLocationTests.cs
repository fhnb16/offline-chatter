using System;
using System.IO;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public sealed class InstallLocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oc-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(@"C:\Users\u\Downloads\OfflineChatter", true)]
    [InlineData(@"C:\Users\u\Desktop", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\Rar$EX01.123\OfflineChatter", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\OfflineChatter", false)]
    [InlineData(@"D:\Tools\OfflineChatter", false)]
    public void Offers_Move_From_Downloads_Desktop_And_Temp(string exeDir, bool expected)
    {
        var roots = new[] { @"C:\Users\u\Desktop", @"C:\Users\u\Downloads" };
        var target = InstallLocation.DefaultTarget(@"C:\Users\u\AppData\Local");
        Assert.Equal(@"C:\Users\u\AppData\Local\Programs\OfflineChatter", target);
        Assert.Equal(expected, InstallLocation.ShouldOfferMove(exeDir, target, roots, @"C:\Users\u\AppData\Local\Temp\"));
    }

    [Fact]
    public void Copies_Exe_And_Data_But_Not_Logs_Or_Partial_Downloads()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "config"));
        Directory.CreateDirectory(Path.Combine(src, "models", "repo"));
        Directory.CreateDirectory(Path.Combine(src, "logs"));
        File.WriteAllText(Path.Combine(src, "OfflineChatter.exe"), "exe");
        File.WriteAllText(Path.Combine(src, "OfflineChatter.exe.config"), "cfg");
        File.WriteAllText(Path.Combine(src, "config", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(src, "models", "repo", "m.gguf"), "gguf");
        File.WriteAllText(Path.Combine(src, "models", "repo", "big.gguf.part"), "partial");
        File.WriteAllText(Path.Combine(src, "logs", "app.jsonl"), "log");

        var dst = Path.Combine(_dir, "dst");
        var exe = InstallLocation.CopyTo(src, dst, "OfflineChatter.exe");

        Assert.Equal(Path.Combine(dst, "OfflineChatter.exe"), exe);
        Assert.True(File.Exists(Path.Combine(dst, "OfflineChatter.exe.config")));
        Assert.True(File.Exists(Path.Combine(dst, "config", "settings.json")));
        Assert.True(File.Exists(Path.Combine(dst, "models", "repo", "m.gguf")));
        Assert.False(File.Exists(Path.Combine(dst, "models", "repo", "big.gguf.part")));
        Assert.False(Directory.Exists(Path.Combine(dst, "logs")));
    }
}
