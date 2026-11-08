using System;
using System.Collections.Generic;
using System.IO;
using OfflineChatter.Core.Engine;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class ServerLogFactsTests
{
    // Фрагменты реального вывода llama-server (Vulkan, Windows), в том числе со старыми префиксами
    private static readonly string[] Log =
    {
        "build: 7083 (a1b2c3d4) with clang version 19.1.5 for x86_64-pc-windows-msvc",
        "system_info: n_threads = 8 (n_threads_batch = 8) / 16 | CPU : SSE3 = 1 | SSSE3 = 1 | AVX = 1 | AVX2 = 1 | F16C = 1 | FMA = 1 | AVX512 = 0 | LLAMAFILE = 1 | OPENMP = 1 | REPACK = 1 |",
        "llama_model_load_from_file_impl: using device Vulkan0 (NVIDIA GeForce RTX 3060) - 11264 MiB free",
        "print_info: file type   = Q4_K - Medium",
        "print_info: file size   = 4.36 GiB (4.91 BPW) ",
        "print_info: arch             = qwen2",
        "print_info: n_ctx_train      = 32768",
        "print_info: n_layer          = 28",
        "print_info: model params     = 7.62 B",
        "print_info: general.name     = Qwen2.5 7B Instruct",
        "load_tensors: offloading 28 repeating layers to GPU",
        "load_tensors: offloaded 29/29 layers to GPU",
        "load_tensors:      Vulkan0 model buffer size =  4168.09 MiB",
        "load_tensors:   CPU_Mapped model buffer size =   292.36 MiB",
        "llama_context: n_seq_max     = 1",
        "llama_context: n_ctx         = 4096",
        "llama_context: n_ctx_per_seq = 4096",
        "llama_context: n_batch       = 2048",
        "llama_context: n_ubatch      = 512",
        "llama_context: flash_attn    = auto",
        "llama_kv_cache:    Vulkan0 KV buffer size =   224.00 MiB",
        "llama_kv_cache: size =  224.00 MiB (  4096 cells,  28 layers,  1/1 seqs), K (f16):  112.00 MiB, V (f16):  112.00 MiB",
        "llama_context:    Vulkan0 compute buffer size =   304.00 MiB",
        "srv          init: initializing slots, n_slots = 1",
        "srv  params_from_: Chat format: Hermes 2 Pro",
        "prompt eval time =     123.45 ms /    56 tokens (    2.20 ms per token,   453.62 tokens per second)",
        "       eval time =    1000.00 ms /   100 tokens (   10.00 ms per token,   100.00 tokens per second)",
        "      total time =    1123.45 ms /   156 tokens",
        "prompt eval time =     100.00 ms /    44 tokens (    2.27 ms per token,   440.00 tokens per second)",
        "       eval time =    1000.00 ms /    50 runs   (   20.00 ms per token,    50.00 tokens per second)",
        "draft acceptance rate = 0.75000 (   30 accepted /    40 generated)",
    };

    [Fact]
    public void Parses_Server_Output()
    {
        var f = ServerLogFacts.Parse(Log);
        Assert.Equal(7083, f.Build);
        Assert.Equal("a1b2c3d4", f.Commit);
        Assert.Equal("clang version 19.1.5", f.Compiler);
        Assert.Equal(8, f.Threads);
        Assert.Equal(16, f.ThreadsTotal);
        Assert.Contains("AVX2", f.CpuFeatures);
        Assert.DoesNotContain("AVX512", f.CpuFeatures);
        Assert.Equal("Vulkan0 (NVIDIA GeForce RTX 3060)", Assert.Single(f.Devices));
        Assert.Equal(11264, f.DeviceFreeMiB["Vulkan0"]);
        Assert.Equal(29, f.OffloadedLayers);
        Assert.Equal(29, f.TotalLayers);
        Assert.True(f.FullyOffloaded);
        Assert.Equal(4168.09, f.ModelBuffersMiB["Vulkan0"], 2);
        Assert.Equal(292.36, f.ModelBuffersMiB["CPU_Mapped"], 2);
        Assert.Equal(224.0, f.KvTotalMiB);
        Assert.Equal(4096, f.KvCells);
        Assert.Equal("f16", f.KvTypeK);
        Assert.Equal(224.0, f.KvBuffersMiB["Vulkan0"]);
        Assert.Equal(304.0, f.ComputeBuffersMiB["Vulkan0"]);
        Assert.Equal(4096, f.ContextInt("n_ctx"));
        Assert.Equal(512, f.ContextInt("n_ubatch"));
        Assert.Equal("auto", f.Context["flash_attn"]);
        Assert.Equal(1, f.Slots);
        Assert.Equal("qwen2", f.Info("arch"));
        Assert.Equal("Q4_K - Medium", f.Info("file type"));
        Assert.Equal("Qwen2.5 7B Instruct", f.Info("general.name"));
        Assert.Equal("Hermes 2 Pro", f.ChatFormat);
    }

    [Fact]
    public void Accumulates_Request_Timings()
    {
        var f = ServerLogFacts.Parse(Log);
        Assert.Equal(2, f.Requests);
        Assert.Equal(100, f.PromptTokensTotal);
        Assert.Equal(150, f.GeneratedTokensTotal);
        Assert.Equal(75.0, f.AvgGenerationTps!.Value, 3); // 150 токенов за 2 с
        Assert.Equal(50, f.LastGeneratedTokens);
        Assert.Equal(50.0, f.LastGenerationTps);
        Assert.Equal(44, f.LastPromptTokens);
        Assert.Equal(0.75, f.LastDraftAcceptance);
    }

    [Fact]
    public void Partial_Offload_And_Old_Prefixes()
    {
        var f = ServerLogFacts.Parse(new[]
        {
            "llm_load_print_meta: n_layer          = 32",
            "llm_load_tensors: offloaded 20/33 layers to GPU",
            "llm_load_tensors:        CPU buffer size =  1200.00 MiB",
            "llama_new_context_with_model: n_ctx      = 8192",
        });
        Assert.False(f.FullyOffloaded);
        Assert.Equal("32", f.Info("n_layer"));
        Assert.Equal(8192, f.ContextInt("n_ctx"));
        Assert.Null(f.Build);
        Assert.Equal(0, f.Requests);
        Assert.Null(f.AvgGenerationTps);
    }

    [Fact]
    public void Clone_Is_Independent()
    {
        var f = ServerLogFacts.Parse(Log);
        var c = f.Clone();
        f.Feed("llama_model_load_from_file_impl: using device CUDA0 (RTX 4090) - 24000 MiB free");
        Assert.Single(c.Devices);
        Assert.Equal(2, f.Devices.Count);
    }

    [Fact]
    public void File_Type_Names()
    {
        Assert.Equal("Q4_K_M", GgufFileTypes.Name(15));
        Assert.Equal("BF16", GgufFileTypes.Name(32));
        Assert.Null(GgufFileTypes.Name(999));
        Assert.Null(GgufFileTypes.Name(null));
    }

    [Fact]
    public void All_Gpu_Layers_Uses_All_Or_999()
    {
        var o = new ServerLaunchOptions { ModelPath = "m.gguf", GpuLayers = -1 };
        var modern = FlagCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "llama-server-help-b12176.txt")));
        var args = o.BuildArgs("127.0.0.1", 8080, null, modern);
        Assert.Equal("all", args[args.IndexOf("--n-gpu-layers") + 1]);

        var old = FlagCatalog.Parse("--model FNAME  m\n--host H   h\n--port P   p\n-ngl, --n-gpu-layers N   number of layers to store in VRAM\n");
        args = o.BuildArgs("127.0.0.1", 8080, null, old);
        Assert.Equal("999", args[args.IndexOf("--n-gpu-layers") + 1]);

        o.GpuLayers = 12;
        args = o.BuildArgs("127.0.0.1", 8080, null, old);
        Assert.Equal("12", args[args.IndexOf("--n-gpu-layers") + 1]);
    }

    [Fact]
    public void Strict_Gpu_Disables_Fit_And_Requires_Full_Offload()
    {
        var resolved = Resolve(new LaunchProfile { GpuLayers = -1, StrictGpu = true, ContextSize = 4096 });
        Assert.True(resolved.Options.RequireFullOffload);
        Assert.False(resolved.Options.Fit);
        Assert.Equal(-1, resolved.Options.GpuLayers);

        // Без «все слои» строгий режим не действует
        var partial = Resolve(new LaunchProfile { GpuLayers = 10, StrictGpu = true, ContextSize = 4096 });
        Assert.False(partial.Options.RequireFullOffload);
    }

    private static ResolvedLaunch Resolve(LaunchProfile p)
    {
        using var tmp = new TempDir();
        var model = tmp.Combine("m.gguf");
        File.WriteAllBytes(model, new byte[1024]);
        var engine = new EngineInstall(tmp.Path, new EngineManifest { Backend = EngineBackend.Vulkan }, false);
        var hw = new HardwareInfo("CPU", 16, 8, true, true, false, 32L << 30, 16L << 30,
            new[] { new GpuInfo("GPU", GpuVendor.Nvidia, 12L << 30, 0, null) }, null, null, true, true, false);
        var flags = FlagCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "llama-server-help-b12176.txt")));
        return ProfileResolver.Resolve(p, new Models.LocalModel { Path = model, SizeBytes = 1024 }, null, hw, engine, flags);
    }
}
