using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class AssetMatcherTests
{
    private static EngineRelease Release() =>
        GitHubReleases.Parse(JToken.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "release-b12176.json"))));

    private static HardwareInfo Hw(bool arm = false, GpuInfo? gpu = null, int? cuda = null, bool vulkan = true) =>
        new("CPU", 8, 4, true, true, false, 16L << 30, 8L << 30, gpu != null ? new[] { gpu } : Array.Empty<GpuInfo>(),
            cuda != null ? "560.00" : null, cuda, vulkan, true, arm);

    [Fact]
    public void Parses_All_Windows_Variants_With_Digest()
    {
        var r = Release();
        Assert.Equal("b12176", r.Tag);
        Assert.Equal(12176, r.BuildNumber);
        var v = AssetMatcher.WindowsVariants(r);
        Assert.Contains(v, x => x.Backend == EngineBackend.Cpu && x.Arch == CpuArch.X64);
        Assert.Contains(v, x => x.Backend == EngineBackend.Cpu && x.Arch == CpuArch.Arm64);
        Assert.Contains(v, x => x.Backend == EngineBackend.Vulkan && x.Arch == CpuArch.X64);
        Assert.Contains(v, x => x.Backend == EngineBackend.Rocm && x.BackendVersion == "10.0");
        Assert.Contains(v, x => x.Backend == EngineBackend.OpenVino);
        Assert.Contains(v, x => x.Backend == EngineBackend.OpenClAdreno && x.Arch == CpuArch.Arm64);
        var cuda124 = Assert.Single(v, x => x.Backend == EngineBackend.Cuda && x.BackendVersion == "12.4");
        Assert.NotNull(cuda124.CudaRuntime);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", cuda124.CudaRuntime!.Name);
        Assert.All(v, x => Assert.False(string.IsNullOrEmpty(x.Asset.Sha256)));
        Assert.DoesNotContain(v, x => x.Asset.Name.Contains("ubuntu"));
    }

    [Fact]
    public void Parses_Win7_Compat_Assets()
    {
        var r = new EngineRelease("llama-b12176-win7", DateTime.Now, true, new[]
        {
            new ReleaseAsset("llama-b12176-bin-win7-cpu-x64.zip", 100, "u1", null),
            new ReleaseAsset("llama-b12176-bin-win7-vulkan-x64.zip", 100, "u2", null),
        });
        Assert.Equal(12176, r.BuildNumber);
        var v = AssetMatcher.WindowsVariants(r);
        Assert.Equal(new[] { EngineBackend.Cpu, EngineBackend.Vulkan }, v.Select(x => x.Backend));
    }

    [Fact]
    public void Parses_Win7_Repository_Release_Asset()
    {
        var r = new EngineRelease("v0.9.0", DateTime.UtcNow, false, new[]
        {
            new ReleaseAsset("llama.cpp-v0.9.0-win7-x64.zip", 100, "u1", "abc"),
        });

        var variant = Assert.Single(AssetMatcher.WindowsVariants(r));
        Assert.Equal(EngineBackend.Cpu, variant.Backend);
        Assert.Equal(CpuArch.X64, variant.Arch);
        Assert.Equal("llama.cpp-v0.9.0-win7-x64.zip", variant.Asset.Name);
    }

    [Theory]
    [InlineData("12.4", 12040, true)]
    [InlineData("12.4", 12080, true)]
    [InlineData("13.5", 12080, false)]
    [InlineData("13.5", 13050, true)]
    [InlineData("12.4", null, false)]
    public void Cuda_Driver_Compatibility(string build, int? driver, bool ok) => Assert.Equal(ok, AssetMatcher.CudaFits(build, driver));

    [Fact]
    public void Win7_Gets_Only_Cpu_And_Vulkan()
    {
        var nv = new GpuInfo("RTX", GpuVendor.Nvidia, 8L << 30, 0, null);
        var applicable = AssetMatcher.WindowsVariants(Release()).Where(v => AssetMatcher.IsApplicable(v, Hw(gpu: nv, cuda: 13050), isWin7: true)).ToList();
        Assert.All(applicable, v => Assert.Contains(v.Backend, new[] { EngineBackend.Cpu, EngineBackend.Vulkan }));
        Assert.All(applicable, v => Assert.Equal(CpuArch.X64, v.Arch));
    }

    [Fact]
    public void Recommends_Cuda_For_Big_Nvidia_Vulkan_For_Others_Cpu_Otherwise()
    {
        var variants = AssetMatcher.WindowsVariants(Release());
        var nv = new GpuInfo("RTX", GpuVendor.Nvidia, 8L << 30, 0, null);
        var rec = AssetMatcher.Recommend(variants, Hw(gpu: nv, cuda: 12080), false)!;
        Assert.Equal(EngineBackend.Cuda, rec.Backend);
        Assert.Equal("12.4", rec.BackendVersion); // 13.5 не подходит драйверу 12.8

        var amd = new GpuInfo("RX", GpuVendor.Amd, 8L << 30, 0, null);
        Assert.Equal(EngineBackend.Vulkan, AssetMatcher.Recommend(variants, Hw(gpu: amd), false)!.Backend);
        Assert.Equal(EngineBackend.Cpu, AssetMatcher.Recommend(variants, Hw(), false)!.Backend);
        Assert.Equal(CpuArch.Arm64, AssetMatcher.Recommend(variants, Hw(arm: true), false)!.Arch);
    }
}

public class FlagCatalogTests
{
    private static FlagCatalog Catalog() =>
        FlagCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "llama-server-help-b12176.txt")));

    [Fact]
    public void Parses_Real_Help()
    {
        var c = Catalog();
        Assert.True(c.Flags.Count > 150, "flags: " + c.Flags.Count);
        foreach (var f in new[] { "--ctx-size", "-c", "--n-gpu-layers", "-ngl", "--fit", "--mmproj", "--api-key", "--jinja", "--no-webui", "--slot-save-path", "--metrics", "--reasoning-format", "--flash-attn" })
            Assert.True(c.Supports(f), f);
        Assert.False(c.Supports("--definitely-not-a-flag"));
        Assert.False(c.Supports("--draft-max")); // помечен как удалённый

        var ctx = c.Find("-c")!;
        Assert.Equal("--ctx-size", ctx.Primary);
        Assert.Equal("N", ctx.Argument);
        Assert.Equal("LLAMA_ARG_CTX_SIZE", ctx.Env);
        Assert.StartsWith("size of the prompt context", ctx.Description);
        Assert.Equal("common params", ctx.Section);
    }

    [Fact]
    public void Launch_Args_Skip_Unsupported_And_Quote_Paths()
    {
        var o = new ServerLaunchOptions
        {
            ModelPath = @"D:\My Models\q.gguf",
            ContextSize = 8192,
            Fit = true,
            ExtraArgs = "--temp 0.7 --foo \"a b\"",
        };
        var catalog = FlagCatalog.Parse("--model FNAME  m\n--ctx-size N   c\n--host H   h\n--port P   p\n--api-key K   k\n--no-webui   w\n");
        var skipped = new List<string>();
        var args = o.BuildArgs("127.0.0.1", 8080, "key", catalog, skipped);
        Assert.Contains("--fit", skipped);
        Assert.Contains("--metrics", skipped);
        Assert.Equal(new[] { "--model", @"D:\My Models\q.gguf", "--host", "127.0.0.1", "--port", "8080", "--api-key", "key", "--ctx-size", "8192", "--no-webui", "--temp", "0.7", "--foo", "a b" }, args);
        var cmd = ServerLaunchOptions.JoinCommandLine(args);
        Assert.Contains("\"D:\\My Models\\q.gguf\"", cmd);
        Assert.Equal(args, ServerLaunchOptions.SplitCommandLine(cmd));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void Quote_Follows_CommandLineToArgv(string arg, string expected) => Assert.Equal(expected, ServerLaunchOptions.Quote(arg));
}

public class EngineInstallerTests
{
    [Fact]
    public void Extract_Flattens_Single_Top_Folder()
    {
        using var tmp = new TempDir();
        var zip = tmp.Combine("a.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntry("build/bin/").ToString();
            using (var w = new StreamWriter(z.CreateEntry("build/llama-server.exe").Open())) w.Write("x");
            using (var w = new StreamWriter(z.CreateEntry("build/sub/ggml.dll").Open())) w.Write("y");
        }
        EngineInstaller.ExtractFlattened(zip, tmp.Combine("out"));
        Assert.True(File.Exists(tmp.Combine("out", "llama-server.exe")));
        Assert.True(File.Exists(tmp.Combine("out", "sub", "ggml.dll")));
    }

    [Fact]
    public void Extract_Rejects_Zip_Slip()
    {
        using var tmp = new TempDir();
        var zip = tmp.Combine("evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("ok.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(z.CreateEntry("../../evil.txt").Open())) w.Write("y");
        }
        Assert.Throws<InvalidDataException>(() => EngineInstaller.ExtractFlattened(zip, tmp.Combine("out")));
        Assert.False(File.Exists(tmp.Combine("evil.txt")));
    }

    [Fact]
    public void Registry_Adds_External_Folder_Even_When_Exe_Is_Nested()
    {
        using var tmp = new TempDir();
        var bin = tmp.Combine("llama.cpp", "build", "bin", "Release");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "llama-server.exe"), "x");
        File.WriteAllText(Path.Combine(bin, "ggml-vulkan.dll"), "x");
        var paths = new AppPaths(tmp.Combine("app"), true);
        paths.EnsureCreated();
        var settings = new EngineSettings();
        var reg = new EngineRegistry(paths, settings);

        var e = reg.AddExternal(tmp.Combine("llama.cpp"));
        Assert.NotNull(e);
        Assert.True(e!.IsExternal);
        Assert.Equal(EngineBackend.Vulkan, e.Manifest.Backend);
        Assert.Equal(e.Id, reg.Active!.Id);
        Assert.Null(reg.AddExternal(tmp.Combine("app")));

        reg.Remove(e);
        Assert.Empty(reg.List());
        Assert.True(File.Exists(Path.Combine(bin, "llama-server.exe"))); // внешнюю папку не трогаем
    }

    /// <summary>OC_LLAMA_DIR=путь к распакованной сборке — проверка smoke-теста на реальном exe.</summary>
    [Fact]
    public async Task Smoke_Test_Real_Build()
    {
        var dir = Environment.GetEnvironmentVariable("OC_LLAMA_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var r = await CompatChecker.SmokeTestAsync(Path.Combine(dir, "llama-server.exe"));
        Assert.True(r.Ok, r.Output);
        Assert.NotNull(r.Build);
        Assert.True(CompatChecker.CheckRuntime(dir).Ok);
    }
}
