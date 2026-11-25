using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Images;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Net;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class SdAssetMatcherTests
{
    private static ReleaseAsset A(string name, long size = 100) => new(name, size, "https://github.com/x/" + name, "abc");

    private static EngineRelease Release() => new("master-1106-532d2c1", DateTime.UtcNow, false, new[]
    {
        A("cudart-sd-bin-win-cu12-x64.zip"),
        A("sd-master-532d2c1-bin-win-cpu-x64.zip"),
        A("sd-master-532d2c1-bin-win-cuda12-x64.zip"),
        A("sd-master-532d2c1-bin-win-rocm-10.1.0-x64.zip"),
        A("sd-master-532d2c1-bin-win-vulkan-x64.zip"),
        A("sd-master-532d2c1-bin-Darwin-macOS-26.7.2-arm64.zip"),
        A("sd-master-532d2c1-bin-Linux-Ubuntu-26.04-x86_64-vulkan.zip"),
    });

    private static HardwareInfo Hw(GpuInfo? gpu = null, int? cuda = null, bool avx512 = false) =>
        new("CPU", 8, 4, true, true, avx512, 16L << 30, 8L << 30, gpu != null ? new[] { gpu } : Array.Empty<GpuInfo>(),
            cuda != null ? "560.00" : null, cuda, true, true, false);

    [Fact]
    public void Parses_Windows_Variants_And_Cuda_Runtime()
    {
        var v = SdAssetMatcher.WindowsVariants(Release());
        Assert.Equal(4, v.Count);
        var cuda = Assert.Single(v, x => x.Backend == EngineBackend.Cuda);
        Assert.Equal("12", cuda.BackendVersion);
        Assert.Equal("cudart-sd-bin-win-cu12-x64.zip", cuda.CudaRuntime?.Name);
        Assert.Contains(v, x => x.Backend == EngineBackend.Rocm && x.BackendVersion == "10.1.0");
        Assert.Contains(v, x => x.Backend == EngineBackend.Vulkan);
        Assert.Contains(v, x => x.Backend == EngineBackend.Cpu && x.BackendVersion == null);
        Assert.DoesNotContain(v, x => x.Asset.Name.Contains("Linux") || x.Asset.Name.Contains("Darwin"));
    }

    [Fact]
    public void Old_Cpu_Variants_By_Instruction_Set()
    {
        var r = new EngineRelease("master-300-abc1234", DateTime.UtcNow, false, new[]
        {
            A("sd-master-abc1234-bin-win-avx2-x64.zip"),
            A("sd-master-abc1234-bin-win-avx512-x64.zip"),
            A("sd-master-abc1234-bin-win-noavx-x64.zip"),
        });
        var v = SdAssetMatcher.WindowsVariants(r);
        Assert.All(v, x => Assert.Equal(EngineBackend.Cpu, x.Backend));
        Assert.False(SdAssetMatcher.IsApplicable(v.Single(x => x.BackendVersion == "avx512"), Hw(), false));
        Assert.Equal("avx2", SdAssetMatcher.Recommend(v, Hw(), false)!.BackendVersion);
        Assert.Equal("avx512", SdAssetMatcher.Recommend(v, Hw(avx512: true), false)!.BackendVersion);
    }

    [Fact]
    public void Recommends_Cuda_For_Nvidia_And_Cpu_Without_Gpu()
    {
        var v = SdAssetMatcher.WindowsVariants(Release());
        var nvidia = new GpuInfo("RTX", GpuVendor.Nvidia, 8L << 30, 0, null);
        Assert.Equal(EngineBackend.Cuda, SdAssetMatcher.Recommend(v, Hw(nvidia, 12080), false)!.Backend);
        Assert.Equal(EngineBackend.Cpu, SdAssetMatcher.Recommend(v, Hw(), false)!.Backend);
        // Старый драйвер без CUDA 12 — Vulkan
        Assert.Equal(EngineBackend.Vulkan, SdAssetMatcher.Recommend(v, Hw(nvidia, 11080), false)!.Backend);
    }

    [Fact]
    public void Build_Number_From_Tag() => Assert.Equal(1106, SdAssetMatcher.BuildNumber("master-1106-532d2c1"));

    [Fact]
    public void Lifts_Nested_Server_Folder()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("bin", "Release"));
        File.WriteAllText(t.Combine("bin", "Release", "sd-server.exe"), "x");
        File.WriteAllText(t.Combine("bin", "Release", "stable-diffusion.dll"), "x");
        SdEngineInstaller.LiftServerFolder(t.Path);
        Assert.True(File.Exists(t.Combine("sd-server.exe")));
        Assert.True(File.Exists(t.Combine("stable-diffusion.dll")));
    }

    [Fact]
    public void Parses_Indented_Help()
    {
        var help = string.Join("\n",
            "usage: sd-server [options]",
            "",
            "Server Options:",
            "  -l, --listen-ip <string>                 server listen ip (default: 127.0.0.1)",
            "  --listen-port <int>                      server listen port (default: 1234)",
            "",
            "Context Options:",
            "  -m, --model <string>                     path to full model",
            "  --diffusion-model <string>               path to the standalone diffusion model",
            "  --vae <string>                           path to standalone vae model",
            "  --llm <string>                           path to the llm text encoder",
            "  --clip_l <string>                        path to the clip-l text encoder",
            "  --offload-to-cpu                         place the weights in RAM to save VRAM, and automatically load them",
            "                                           into VRAM when needed",
            "  -t, --threads <int>                      number of threads",
            "  --flash-attn <string>                    on, off or auto");
        var c = SdHelp.Parse(help);
        Assert.NotNull(c);
        Assert.True(c!.Supports("--listen-port"));
        Assert.True(c.Supports("-m"));
        Assert.True(c.Supports("--clip_l"));
        Assert.Contains("into VRAM", c.Find("--offload-to-cpu")!.Description);
        Assert.False(c.Supports("--diffusion-fa"));
        Assert.Null(SdHelp.Parse("garbage"));
    }
}

public class ImagePresetTests
{
    [Theory]
    [InlineData("z_image_turbo-Q4_K.gguf", ImageFamily.ZImageTurbo)]
    [InlineData("Qwen-Image-Edit-2509-Q3_K_S.gguf", ImageFamily.QwenImageEdit)]
    [InlineData("qwen_image_edit_2511_q4.gguf", ImageFamily.QwenImageEdit2511)]
    [InlineData("qwen-image-2512-Q3_K_M.gguf", ImageFamily.QwenImage)]
    [InlineData("flux1-schnell-q4_k.gguf", ImageFamily.Flux1Schnell)]
    [InlineData("flux1-kontext-dev-q8_0.gguf", ImageFamily.FluxKontext)]
    [InlineData("flux-2-klein-4b.safetensors", ImageFamily.Flux2Klein)]
    [InlineData("flux1-dev-q8_0.gguf", ImageFamily.Flux1Dev)]
    [InlineData("stable-diffusion-xl-base-1.0-Q4_0.gguf", ImageFamily.Sdxl)]
    [InlineData("sd_xl_turbo_1.0_fp16.safetensors", ImageFamily.SdxlTurbo)]
    [InlineData("stable-diffusion-v1-5-pruned-emaonly-Q8_0.gguf", ImageFamily.Sd15)]
    [InlineData("sd3.5_large.safetensors", ImageFamily.Sd3)]
    [InlineData("chroma-unlocked-v40-q8_0.gguf", ImageFamily.Chroma)]
    public void Detects_Family(string name, ImageFamily expected) => Assert.Equal(expected, ImagePresets.Detect(name));

    [Fact]
    public void Unknown_Name_Is_Not_Guessed() => Assert.Null(ImagePresets.Detect("my-model.gguf"));

    [Theory]
    [InlineData("ae.safetensors", ImageComponent.Vae)]
    [InlineData("qwen_image_vae.safetensors", ImageComponent.Vae)]
    [InlineData("sdxl_vae.safetensors", ImageComponent.Vae)]
    [InlineData("clip_l.safetensors", ImageComponent.ClipL)]
    [InlineData("t5xxl_fp8_e4m3fn.safetensors", ImageComponent.T5xxl)]
    [InlineData("Qwen3-4B-Instruct-2507-Q4_K_M.gguf", ImageComponent.Llm)]
    [InlineData("Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf", ImageComponent.Llm)]
    [InlineData("Qwen2.5-VL-7B-Instruct.mmproj-f16.gguf", ImageComponent.LlmVision)]
    [InlineData("z_image_turbo-Q4_K.gguf", ImageComponent.DiffusionModel)]
    [InlineData("stable-diffusion-v1-5-pruned-emaonly-Q8_0.gguf", ImageComponent.Checkpoint)]
    public void Guesses_Component(string name, ImageComponent expected) => Assert.Equal(expected, ImagePresets.GuessComponent(name));

    [Fact]
    public void Diffusers_Parts_Are_Hidden()
    {
        Assert.True(ImagePresets.IsDiffusersPart("unet/diffusion_pytorch_model.safetensors"));
        Assert.True(ImagePresets.IsDiffusersPart("text_encoder/model.safetensors"));
        Assert.False(ImagePresets.IsDiffusersPart("split_files/vae/ae.safetensors"));
        Assert.False(ImagePresets.IsDiffusersPart("sd_xl_base_1.0.safetensors"));
    }

    [Fact]
    public void Aspect_Sizes_Keep_Area_And_Multiple_Of_64()
    {
        var p = ImagePresets.Get(ImageFamily.Sdxl);
        var (w, h) = ImagePresets.SizeFor(p, 16, 9);
        Assert.Equal(0, w % 64);
        Assert.Equal(0, h % 64);
        Assert.True(w > h);
        Assert.InRange(w * h, p.BaseArea * 0.85, p.BaseArea * 1.15);
        Assert.Equal((512, 512), ImagePresets.SizeFor(ImagePresets.Get(ImageFamily.Sd15), 1, 1));
    }

    [Fact]
    public void Kits_Are_Complete_For_Their_Family()
    {
        foreach (var kit in ImageKits.All)
        {
            var roles = kit.Files.Select(f => f.Role).ToList();
            Assert.All(kit.Preset.Required, r => Assert.Contains(r, roles));
            Assert.All(kit.Files, f => Assert.True(f.Size > 0));
            Assert.All(kit.Files, f => Assert.DoesNotContain("black-forest-labs", f.Repo)); // закрытый репозиторий
        }
        Assert.Single(ImageKits.All, k => k.Recommended);
    }

    [Fact]
    public void Preset_Defaults_Apply_And_Drop_Edit_Mode_When_Unsupported()
    {
        var g = new ImageGenParams { Prompt = "cat", Mode = ImageMode.Edit };
        g.ApplyPreset(ImagePresets.Get(ImageFamily.ZImageTurbo));
        Assert.Equal(8, g.Steps);
        Assert.Equal(1.0, g.Cfg);
        Assert.Equal("cat", g.Prompt);
        Assert.Equal(ImageMode.TextToImage, g.Mode);
        Assert.False(ImagePresets.Get(ImageFamily.ZImageTurbo).UsesNegativePrompt);
        Assert.True(ImagePresets.Get(ImageFamily.Sdxl).UsesNegativePrompt);
    }
}

public class ImageModelTests
{
    [Fact]
    public void Describe_Assigns_Roles_And_Family()
    {
        using var t = new TempDir();
        var files = new[] { "z_image_turbo-Q4_K.gguf", "ae.safetensors", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf" }.Select(f =>
        {
            var p = t.Combine(f);
            File.WriteAllText(p, "x");
            return p;
        }).ToList();
        var m = ImageModelRegistry.Describe(files);
        Assert.Equal(nameof(ImageFamily.ZImageTurbo), m.Family);
        Assert.Equal(files[0], m.Files[ImageComponent.DiffusionModel]);
        Assert.Equal(files[1], m.Files[ImageComponent.Vae]);
        Assert.Equal(files[2], m.Files[ImageComponent.Llm]);
        Assert.True(m.IsReady);
        File.Delete(files[1]);
        Assert.Equal(new[] { ImageComponent.Vae }, m.Missing());
    }

    [Fact]
    public void Registry_Roundtrip_And_Shared_Files_Kept_On_Delete()
    {
        using var t = new TempDir();
        var vae = t.Combine("ae.safetensors");
        var a = t.Combine("z_image_turbo-Q4_K.gguf");
        var b = t.Combine("flux1-schnell-q4_k.gguf");
        foreach (var f in new[] { vae, a, b }) File.WriteAllText(f, "x");
        var reg = new ImageModelRegistry(t.Combine("image-models.json"));
        var ma = reg.AddOrUpdate(ImageModelRegistry.Describe(new[] { a, vae }));
        reg.AddOrUpdate(ImageModelRegistry.Describe(new[] { b, vae }));
        var again = new ImageModelRegistry(t.Combine("image-models.json"));
        Assert.Equal(2, again.Models.Count);
        Assert.Equal(vae, again.Find(ma.Id)!.Files[ImageComponent.Vae]);
        again.Remove(again.Find(ma.Id)!, deleteFiles: true);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(vae)); // нужен второй модели
    }

    [Fact]
    public void Same_Main_File_Updates_Existing()
    {
        using var t = new TempDir();
        var a = t.Combine("z_image_turbo-Q4_K.gguf");
        File.WriteAllText(a, "x");
        var reg = new ImageModelRegistry(t.Combine("m.json"));
        var first = reg.AddOrUpdate(ImageModelRegistry.Describe(new[] { a }));
        var second = reg.AddOrUpdate(ImageModelRegistry.Describe(new[] { a }));
        Assert.Same(first, second);
        Assert.Single(reg.Models);
    }
}

public class ImageServerOptionsTests
{
    private static ImageModel Model() => new()
    {
        Family = nameof(ImageFamily.QwenImage),
        Files = new Dictionary<ImageComponent, string>
        {
            [ImageComponent.Llm] = @"C:\m\qwen vl.gguf",
            [ImageComponent.DiffusionModel] = @"C:\m\qwen-image.gguf",
            [ImageComponent.Vae] = @"C:\m\vae.safetensors",
        },
    };

    [Fact]
    public void Builds_Args_From_Preset()
    {
        var o = ImageServerOptions.Resolve(Model(), new ImageLaunchSettings());
        var args = o.BuildArgs("127.0.0.1", 5555, null);
        var cmd = string.Join(" ", args);
        Assert.True(args.IndexOf("--diffusion-model") < args.IndexOf("--vae"));
        Assert.Contains("--listen-port 5555", cmd);
        Assert.Contains("--offload-to-cpu", args); // пресет Qwen-Image
        Assert.Contains("--flow-shift 3", cmd);
        Assert.Contains("--cfg-scale 2.5", cmd);
        Assert.Contains("--sampling-method euler", cmd);
        Assert.DoesNotContain("--clip-on-cpu", args);
        Assert.Contains("--llm", args);
    }

    [Fact]
    public void User_Settings_Override_Preset_And_Unknown_Flags_Are_Skipped()
    {
        var flags = SdHelp.Parse(string.Join("\n",
            "Options:",
            "  --diffusion-model <string>   a", "  --vae <string>   a", "  --llm <string>   a", "  --listen-ip <string>   a",
            "  --listen-port <int>   a", "  --steps <int>   a", "  --cfg-scale <float>   a", "  -W, --width <int>   a", "  -H, --height <int>   a",
            "  --diffusion-fa   use flash attention"));
        var o = ImageServerOptions.Resolve(Model(), new ImageLaunchSettings { OffloadToCpu = false, FlashAttention = "on", ModelArgs = "x=1" });
        var skipped = new List<string>();
        var args = o.BuildArgs("127.0.0.1", 1, flags, skipped);
        Assert.DoesNotContain("--offload-to-cpu", args);
        Assert.Contains("--diffusion-fa", args); // старая сборка: --flash-attn нет
        Assert.Contains("--model-args", skipped);
        Assert.Contains("--sampling-method", skipped);
        Assert.Contains("-W", args);
    }

    [Fact]
    public void Model_Args_Merge_Preset_And_User()
    {
        var m = Model();
        m.Family = nameof(ImageFamily.QwenImageEdit2511);
        var o = ImageServerOptions.Resolve(m, new ImageLaunchSettings { ModelArgs = "foo=bar", ServerDefaults = false });
        Assert.Equal("qwen_image_zero_cond_t=true,foo=bar", o.ModelArgs);
        Assert.DoesNotContain("--steps", o.BuildArgs("127.0.0.1", 1, null));
    }

    [Theory]
    [InlineData("  |==========>         | 3/20 - 1.50s/it", 3, 20, 1.5)]
    [InlineData("|==================================================| 8/8 - 2.00it/s", 8, 8, 0.5)]
    public void Parses_Progress(string line, int step, int total, double spi)
    {
        var p = ImageProgress.Parse(line)!;
        Assert.Equal(step, p.Step);
        Assert.Equal(total, p.Total);
        Assert.Equal(spi, p.SecondsPerStep!.Value, 3);
    }

    [Fact]
    public void Non_Progress_Line() => Assert.Null(ImageProgress.Parse("[INFO ] loading model"));
}

public class ImageClientTests
{
    [Fact]
    public void Chooses_Route_By_Mode_And_Images()
    {
        var p = new ImageGenParams { Mode = ImageMode.Edit };
        Assert.Equal(ImageClient.Route.Txt2Img, ImageClient.Choose(new ImageRequest { Params = p }));
        Assert.Equal(ImageClient.Route.Edits, ImageClient.Choose(new ImageRequest { Params = p, RefImages = new[] { new byte[] { 1 } } }));
        p.Mode = ImageMode.ImageToImage;
        Assert.Equal(ImageClient.Route.Img2Img, ImageClient.Choose(new ImageRequest { Params = p, InitImage = new byte[] { 1 } }));
    }

    [Fact]
    public void Txt2Img_Body_Has_All_Fields()
    {
        var o = ImageClient.BuildTxt2Img(new ImageGenParams
        {
            Prompt = "cat", NegativePrompt = "blurry", Width = 768, Height = 512, Steps = 12, Cfg = 4.5, Sampler = "euler_a", Seed = 42, BatchCount = 2, ClipSkip = 2,
        });
        Assert.Equal("cat", (string?)o["prompt"]);
        Assert.Equal("blurry", (string?)o["negative_prompt"]);
        Assert.Equal(768, (int)o["width"]!);
        Assert.Equal(12, (int)o["steps"]!);
        Assert.Equal(4.5, (double)o["cfg_scale"]!);
        Assert.Equal(42, (long)o["seed"]!);
        Assert.Equal(2, (int)o["batch_size"]!);
        Assert.Equal("euler_a", (string?)o["sampler_name"]);
        Assert.Null(o["scheduler"]);
    }

    [Fact]
    public void Extra_Args_Block_Is_Valid_Json()
    {
        var s = ImageClient.BuildExtraArgs(new ImageGenParams { Steps = 9, Cfg = 1, Sampler = "euler", FlowShift = 3, Seed = 7, NegativePrompt = "x" });
        Assert.StartsWith("<sd_cpp_extra_args>", s);
        Assert.EndsWith("</sd_cpp_extra_args>", s);
        var json = JObject.Parse(s.Substring(19, s.Length - 19 - 20));
        Assert.Equal(9, (int)json["sample_params"]!["sample_steps"]!);
        Assert.Equal(3.0, (double)json["sample_params"]!["flow_shift"]!);
        Assert.Equal(1.0, (double)json["sample_params"]!["guidance"]!["txt_cfg"]!);
        Assert.Equal("x", (string?)json["negative_prompt"]);
    }

    [Fact]
    public void Parses_Images_From_All_Answer_Shapes()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        Assert.Single(ImageClient.ParseImages(JToken.Parse($"{{\"images\":[\"{b64}\"],\"info\":\"\"}}")));
        Assert.Equal(2, ImageClient.ParseImages(JToken.Parse($"{{\"data\":[{{\"b64_json\":\"{b64}\"}},{{\"b64_json\":\"data:image/png;base64,{b64}\"}}]}}")).Count);
        Assert.Single(ImageClient.ParseImages(JToken.Parse($"{{\"result\":{{\"images\":[{{\"b64_json\":\"{b64}\"}}]}}}}")));
        Assert.Empty(ImageClient.ParseImages(JToken.Parse("{\"images\":[\"%%%\"]}")));
    }

    [Fact]
    public void Error_Text_From_Json()
    {
        Assert.Equal("model_not_loaded", ImageClient.ErrorText("{\"error\":{\"message\":\"model_not_loaded\"}}"));
        Assert.Equal("plain", ImageClient.ErrorText("plain"));
    }

    [Fact]
    public async Task Falls_Back_To_OpenAi_When_Native_And_Sdapi_Missing()
    {
        var b64 = Convert.ToBase64String(new byte[] { 9 });
        var seen = new List<string>();
        var handler = new StubHandler(req =>
        {
            seen.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri.AbsolutePath is "/sdapi/v1/txt2img" or "/sdcpp/v1/img_gen"
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"data\":[{{\"b64_json\":\"{b64}\"}}]}}") };
        });
        var client = new ImageClient(new HttpClient(handler), "http://127.0.0.1:1");
        var r = await client.GenerateAsync(new ImageRequest { Params = new ImageGenParams { Prompt = "a" } }, CancellationToken.None);
        Assert.Equal("/v1/images/generations", r.Endpoint);
        Assert.Equal(new[] { "/sdcpp/v1/img_gen", "/sdapi/v1/txt2img", "/v1/images/generations" }, seen);
        Assert.Equal(new byte[] { 9 }, r.Images[0]);
    }

    [Fact]
    public void Native_Body_Has_Images_Sigmas_Lora_And_Tiling()
    {
        var p = new ImageGenParams
        {
            Prompt = "replace the jacket",
            Mode = ImageMode.ImageToImage,
            Width = 1024,
            Height = 768,
            Steps = 20,
            Cfg = 1,
            Seed = 5,
            Strength = 1.7,
            Sigmas = ImageSigmas.Format(ImageSigmas.QwenImage21Turbo),
            Transparent = true,
            Loras = { new ImageLora { Path = @"qwen\fast.safetensors", Multiplier = 0.8 } },
        };
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 };
        var o = ImageClient.BuildNative(new ImageRequest
        {
            Params = p,
            InitImage = png,
            MaskImage = png,
            RefImages = new[] { png, new byte[] { 0xFF, 0xD8, 1 } },
            PromptImages = new Dictionary<string, byte[]> { ["1"] = png },
            Preview = "tae",
            VaeTiling = true,
        });
        Assert.StartsWith(TransparentPrompt.Prefix, (string?)o["prompt"]);
        Assert.Equal(8, (int)o["sample_params"]!["sample_steps"]!); // шаги — из сигм
        Assert.Equal(9, ((JArray)o["sample_params"]!["custom_sigmas"]!).Count);
        Assert.Equal(1.0, (double)o["strength"]!); // зажато в 0..1
        Assert.StartsWith("data:image/png;base64,", (string?)o["init_image"]);
        Assert.StartsWith("data:image/png;base64,", (string?)o["mask_image"]);
        Assert.StartsWith("data:image/jpeg;base64,", (string?)o["ref_images"]![1]);
        Assert.NotNull(o["prompt_images"]!["1"]);
        Assert.Equal("qwen/fast.safetensors", (string?)o["lora"]![0]!["path"]);
        Assert.Equal(512, (int)o["vae_tiling_params"]!["tile_size_w"]!);
        Assert.Equal("tae", (string?)o["preview"]);
        Assert.Null(o["cache_mode"]);
        Assert.True((bool)o["embed_image_metadata"]!);
    }

    [Fact]
    public void Native_Body_Without_Images_Has_No_Image_Fields()
    {
        var o = ImageClient.BuildNative(new ImageRequest { Params = new ImageGenParams { Prompt = "cat", Strength = 0.5 } });
        Assert.Null(o["init_image"]);
        Assert.Null(o["strength"]);
        Assert.Null(o["ref_images"]);
        Assert.Null(o["prompt_images"]);
        Assert.Null(o["vae_tiling_params"]);
        Assert.Equal("cat", (string?)o["prompt"]);
    }

    [Fact]
    public void Parses_Job_Progress()
    {
        var png = Convert.ToBase64String(new byte[] { 7, 7 });
        var j = JToken.Parse("{\"id\":\"job_1\",\"status\":\"generating\",\"queue_position\":0," +
                             "\"progress\":{\"stage\":\"sampling\",\"detail\":\"\",\"pass\":1,\"completed\":3,\"total\":8,\"unit\":\"steps\",\"stage_elapsed_ms\":3000,\"elapsed_ms\":7000}," +
                             $"\"preview\":{{\"pass\":1,\"step\":3,\"total_steps\":8,\"b64_json\":\"{png}\"}}}}");
        var p = ImageJobProgress.Parse(j);
        Assert.Equal("sampling", p.Stage);
        Assert.Equal(3, p.Completed);
        Assert.Equal(8, p.Total);
        Assert.Equal(new byte[] { 7, 7 }, p.Preview);
        Assert.False(p.IsTerminal);
        var unknown = ImageJobProgress.Parse(JToken.Parse("{\"status\":\"queued\",\"progress\":{\"stage\":\"conditioning\",\"completed\":null,\"total\":null},\"preview\":null}"));
        Assert.Null(unknown.Completed);
        Assert.Null(unknown.Preview);
        Assert.Null(ImageJobProgress.PreviewKey(JToken.Parse("{\"preview\":null}")));
    }

    [Fact]
    public async Task Native_Job_Polls_Progress_And_Returns_Images()
    {
        var img = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var polls = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/sdcpp/v1/img_gen") return Json(HttpStatusCode.Accepted, "{\"id\":\"job_1\",\"status\":\"queued\"}");
            if (path != "/sdcpp/v1/jobs/job_1") return Json(HttpStatusCode.NotFound, "{}");
            polls++;
            return polls switch
            {
                1 => Json(HttpStatusCode.OK, "{\"status\":\"generating\",\"progress\":{\"stage\":\"conditioning\",\"stage_elapsed_ms\":100,\"elapsed_ms\":600}}"),
                2 => Json(HttpStatusCode.OK, "{\"status\":\"generating\",\"progress\":{\"stage\":\"sampling\",\"completed\":4,\"total\":8,\"stage_elapsed_ms\":4000,\"elapsed_ms\":6500}," +
                                              $"\"preview\":{{\"pass\":1,\"step\":4,\"total_steps\":8,\"b64_json\":\"{img}\"}}}}"),
                _ => Json(HttpStatusCode.OK, "{\"status\":\"completed\",\"progress\":{\"stage\":\"encoding\",\"stage_elapsed_ms\":0,\"elapsed_ms\":9000}," +
                                              $"\"result\":{{\"output_format\":\"png\",\"images\":[{{\"index\":0,\"b64_json\":\"{img}\",\"generation_info\":\"seed = 5\"}}]}}}}"),
            };
        });
        var client = new ImageClient(new HttpClient(handler), "http://127.0.0.1:1") { PollInterval = TimeSpan.FromMilliseconds(1) };
        var reports = new List<ImageJobProgress>();
        var r = await client.GenerateAsync(new ImageRequest { Params = new ImageGenParams { Prompt = "a", Seed = 5 } }, CancellationToken.None,
            new SyncProgress<ImageJobProgress>(reports.Add));
        Assert.Equal("/sdcpp/v1/img_gen", r.Endpoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Images[0]);
        Assert.Equal("seed = 5", r.GenerationInfo[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), r.ConditioningTime); // 500 → 2500
        Assert.Contains(reports, p => p.Preview != null && p.Completed == 4);
        Assert.Equal(1.0, r.SecondsPerStep!.Value, 3); // 4 шага за 4 с
    }

    [Fact]
    public async Task Native_Job_Cancel_Is_Sent_To_Server()
    {
        var cancelled = false;
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/sdcpp/v1/img_gen") return Json(HttpStatusCode.Accepted, "{\"id\":\"job_2\"}");
            if (path == "/sdcpp/v1/jobs/job_2/cancel")
            {
                cancelled = true;
                return Json(HttpStatusCode.OK, "{\"cancel_requested\":true}");
            }
            cts.Cancel();
            return Json(HttpStatusCode.OK, cancelled ? "{\"status\":\"cancelled\"}" : "{\"status\":\"generating\",\"progress\":{\"stage\":\"sampling\"}}");
        });
        var client = new ImageClient(new HttpClient(handler), "http://127.0.0.1:1") { PollInterval = TimeSpan.FromMilliseconds(1) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(new ImageRequest { Params = new ImageGenParams { Prompt = "a" } }, cts.Token));
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Native_Job_Failure_Has_Server_Message()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath == "/sdcpp/v1/img_gen"
            ? Json(HttpStatusCode.Accepted, "{\"id\":\"job_3\"}")
            : Json(HttpStatusCode.OK, "{\"status\":\"failed\",\"error\":{\"code\":\"generation_failed\",\"message\":\"out of memory\"}}"));
        var client = new ImageClient(new HttpClient(handler), "http://127.0.0.1:1") { PollInterval = TimeSpan.FromMilliseconds(1) };
        var ex = await Assert.ThrowsAsync<ImageApiException>(() => client.GenerateAsync(new ImageRequest { Params = new ImageGenParams { Prompt = "a" } }, CancellationToken.None));
        Assert.Equal("out of memory", ex.Message);
    }

    [Fact]
    public void Legacy_Img2Img_Carries_Mask_And_Extra_Args()
    {
        var o = ImageClient.BuildImg2Img(new ImageRequest
        {
            Params = new ImageGenParams { Prompt = "x", Sigmas = "1,0.5,0", CacheMode = "easycache" },
            InitImage = new byte[] { 1 },
            MaskImage = new byte[] { 2 },
        });
        Assert.Equal(Convert.ToBase64String(new byte[] { 2 }), (string?)o["mask"]);
        var prompt = (string?)o["prompt"]!;
        Assert.Contains("custom_sigmas", prompt);
        Assert.Contains("easycache", prompt);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_f(request));
    }
}

public class QwenImage21Tests
{
    [Theory]
    [InlineData("Qwen-Image-2.1-Turbo-AD-Q4_K.gguf", ImageFamily.QwenImage21Turbo)]
    [InlineData("qwen_image_2.1_turbo_int8_convrot.safetensors", ImageFamily.QwenImage21Turbo)]
    [InlineData("qwen_image_2.1-Q4_K.gguf", ImageFamily.QwenImage21)]
    [InlineData("leejet/Qwen-Image-2.1-GGUF", ImageFamily.QwenImage21)]
    [InlineData("qwen-image-2512-Q3_K_M.gguf", ImageFamily.QwenImage)]
    public void Detects_Family(string name, ImageFamily expected) => Assert.Equal(expected, ImagePresets.Detect(name));

    [Theory]
    [InlineData("taeqi2_1.safetensors", ImageComponent.Tae)]
    [InlineData("taesdxl.safetensors", ImageComponent.Tae)]
    [InlineData("qwen_image_2.1_vae_bf16.safetensors", ImageComponent.Vae)]
    [InlineData("Qwen3-VL-8B-Instruct-UD-Q4_K_XL.gguf", ImageComponent.Llm)]
    [InlineData("Qwen3VL-8B-Instruct-Q4_K_M.gguf", ImageComponent.Llm)]
    [InlineData("qwen3vl_8b_int8_convrot.safetensors", ImageComponent.Llm)]
    [InlineData("mmproj-F16.gguf", ImageComponent.LlmVision)]
    [InlineData("Qwen-Image-2.1-Turbo-AD-Q4_K.gguf", ImageComponent.DiffusionModel)]
    public void Guesses_Component(string name, ImageComponent expected) => Assert.Equal(expected, ImagePresets.GuessComponent(name));

    [Fact]
    public void Turbo_Preset_Uses_Fixed_Sigmas_And_32_Multiple()
    {
        var p = ImagePresets.Get(ImageFamily.QwenImage21Turbo);
        Assert.True(p.IsDistilled);
        Assert.True(p.SupportsAlpha);
        Assert.True(p.SupportsPromptImages);
        Assert.Equal(8, ImageSigmas.Steps(p.Sigmas!));
        var g = new ImageGenParams { Mode = ImageMode.Edit, CacheMode = "easycache", Transparent = true };
        g.ApplyPreset(p);
        Assert.Equal(8, g.Steps);
        Assert.Equal("1,0.978453,0.95418,0.926626,0.89508,0.845148,0.704534,0.414568,0", g.Sigmas);
        Assert.Equal(string.Empty, g.CacheMode);
        Assert.True(g.Transparent);
        Assert.Equal(ImageMode.Edit, g.Mode);
        var (w, h) = ImagePresets.SizeFor(p, 3, 2, 2.0);
        Assert.Equal(0, w % 32);
        Assert.Equal(0, h % 32);
        Assert.InRange(w * h, 2048 * 2048 * 0.95, 2048 * 2048 * 1.05);
        // Остальные пресеты: прежняя сетка 64
        Assert.Equal(64, ImagePresets.Get(ImageFamily.Sdxl).RoundTo);
    }

    [Fact]
    public void Base_Preset_Has_No_Sigmas_And_Drops_Alpha_For_Other_Families()
    {
        var g = new ImageGenParams { Transparent = true, Sigmas = "1,0" };
        g.ApplyPreset(ImagePresets.Get(ImageFamily.QwenImage21));
        Assert.Equal(string.Empty, g.Sigmas);
        Assert.True(g.Transparent);
        g.ApplyPreset(ImagePresets.Get(ImageFamily.ZImageTurbo));
        Assert.False(g.Transparent);
    }

    [Fact]
    public void Vae_Or_Tae_Is_Enough()
    {
        using var t = new TempDir();
        string F(string n)
        {
            var p = t.Combine(n);
            File.WriteAllText(p, "x");
            return p;
        }
        var m = new ImageModel
        {
            Family = nameof(ImageFamily.QwenImage21Turbo),
            Files = { [ImageComponent.DiffusionModel] = F("dit.gguf"), [ImageComponent.Llm] = F("Qwen3-VL-8B.gguf") },
        };
        Assert.Equal(new[] { ImageComponent.Vae }, m.Missing());
        m.Files[ImageComponent.Tae] = F("taeqi2_1.safetensors");
        Assert.True(m.IsReady);
        Assert.True(m.NeedsVisionForEdit); // GGUF-кодировщик без mmproj
        m.Files[ImageComponent.LlmVision] = F("mmproj-F16.gguf");
        Assert.False(m.NeedsVisionForEdit);
    }

    [Fact]
    public void Kits_Have_Vae_Or_Tae_And_Ram_Requirements()
    {
        var kits = ImageKits.All.Where(k => k.Family == ImageFamily.QwenImage21Turbo).ToList();
        Assert.Equal(3, kits.Count);
        Assert.All(kits, k =>
        {
            Assert.Contains(k.Files, f => f.Role == ImageComponent.LlmVision);
            Assert.Contains(k.Files, f => f.Role == ImageComponent.Tae);
            Assert.True(k.MinRam >= 16L << 30);
        });
        Assert.Contains(kits, k => k.Files.Any(f => f.Path == "Qwen-Image-2.1-Turbo-AD-Q4_K.gguf" && f.Size == 4201694944));
    }

    [Fact]
    public void Server_Args_With_Vae_And_Tae()
    {
        var m = new ImageModel
        {
            Family = nameof(ImageFamily.QwenImage21Turbo),
            Files =
            {
                [ImageComponent.DiffusionModel] = @"C:\m\dit.gguf",
                [ImageComponent.Llm] = @"C:\m\llm.gguf",
                [ImageComponent.LlmVision] = @"C:\m\mmproj.gguf",
                [ImageComponent.Vae] = @"C:\m\vae.safetensors",
                [ImageComponent.Tae] = @"C:\m\taeqi2_1.safetensors",
            },
        };
        var args = ImageServerOptions.Resolve(m, new ImageLaunchSettings { MaxVram = "6,8", Mmap = true }).BuildArgs("127.0.0.1", 1, null);
        var cmd = string.Join(" ", args);
        Assert.Contains(@"--taesd C:\m\taeqi2_1.safetensors", cmd);
        Assert.Contains("--taesd-preview-only", args);
        Assert.Contains("--llm_vision", args);
        Assert.Contains("--max-vram 6.8", cmd);
        Assert.Contains("--conditioning-cache-size 8", cmd); // пресет
        Assert.Contains("--mmap", args);
        Assert.Contains("--sigmas 1,0.978453,", cmd);

        // Только TAE — он и декодирует итог
        m.Files.Remove(ImageComponent.Vae);
        Assert.DoesNotContain("--taesd-preview-only", ImageServerOptions.Resolve(m, new ImageLaunchSettings()).BuildArgs("127.0.0.1", 1, null));
        Assert.Equal("cuda0=6,vulkan0=4", ImageServerOptions.NormalizeVram(" cuda0=6, vulkan0=4 "));
        Assert.Null(ImageServerOptions.NormalizeVram(" "));
    }

    [Fact]
    public void Memory_Profiles_Fill_Launch_Settings()
    {
        var s = new ImageLaunchSettings { ExtraArgs = "--foo", Threads = 6 };
        ImageMemoryProfiles.Apply(s, ImageMemoryProfile.Balanced, 8L << 30);
        Assert.Equal("6.8", s.MaxVram);
        Assert.True(s.OffloadToCpu);
        Assert.Equal("on", s.FlashAttention);
        Assert.Equal(8, s.ConditioningCacheSize);
        Assert.Null(s.ClipOnCpu);
        Assert.Equal("--foo", s.ExtraArgs);
        Assert.Equal(6, s.Threads);
        ImageMemoryProfiles.Apply(s, ImageMemoryProfile.EncoderOnCpu, 12L << 30);
        Assert.True(s.ClipOnCpu);
        Assert.Equal("10.2", s.MaxVram);
        Assert.Null(s.ParamsBackend);
        ImageMemoryProfiles.Apply(s, ImageMemoryProfile.SaveRam, 0);
        Assert.True(s.Mmap);
        Assert.Equal("te=disk", s.ParamsBackend);
        Assert.Null(s.MaxVram);
        Assert.Equal(2.0, ImageMemoryProfiles.VramBudgetGb(1L << 30));
        ImageMemoryProfiles.Apply(s, ImageMemoryProfile.Balanced, 8L << 30);
        Assert.Null(s.ParamsBackend);
    }

    [Fact]
    public void Params_Backend_Goes_After_Offload()
    {
        var model = new ImageModel
        {
            Family = nameof(ImageFamily.QwenImage21Turbo),
            Files = { [ImageComponent.DiffusionModel] = "d.gguf", [ImageComponent.Llm] = "l.gguf", [ImageComponent.Vae] = "v.safetensors" },
        };
        var o = ImageServerOptions.Resolve(model, new ImageLaunchSettings { OffloadToCpu = true, ParamsBackend = " te = disk " });
        Assert.Equal("te=disk", o.ParamsBackend);
        var args = o.BuildArgs("127.0.0.1", 1234, null);
        var i = args.IndexOf("--params-backend");
        Assert.True(i > args.IndexOf("--offload-to-cpu"));
        Assert.Equal("te=disk", args[i + 1]);
        Assert.DoesNotContain("--params-backend", ImageServerOptions.Resolve(model, new ImageLaunchSettings()).BuildArgs("127.0.0.1", 1, null));
    }

    [Fact]
    public void Failures_Classified_From_Server_Output()
    {
        Assert.Equal(ImageFailureKind.RamExhausted, ImageFailures.Classify(new[]
        {
            "[I] loading tensors completed --- model_loader.cpp:1456",
            "[E] RAM graph capacity exceeded. Requested 1784448 bytes, 0 bytes available, 0 bytes resident --- model_manager.cpp:1359",
            "[E] LLM prompt encoding failed --- conditioner.hpp:2462",
        }));
        Assert.Equal(ImageFailureKind.RamExhausted, ImageFailures.Classify(new[]
            { "[E] RAM weight capacity exceeded. Requested 155895168 bytes, 65134592 bytes available, 0 bytes resident" }));
        Assert.Equal(ImageFailureKind.VramExhausted, ImageFailures.Classify(new[]
            { "[E] CUDA0 weight capacity exceeded. Requested 1 bytes, 0 bytes available" }));
        Assert.Equal(ImageFailureKind.VramExhausted, ImageFailures.Classify(new[] { "CUDA error: out of memory" }));
        Assert.Equal(ImageFailureKind.Unknown, ImageFailures.Classify(new[] { "[E] vae encode compute failed --- vae.hpp:294" }));
    }

    [Fact]
    public void Sigmas_Parse_And_Format()
    {
        Assert.Equal(new[] { 1.0, 0.5, 0.0 }, ImageSigmas.Parse("1, 0.5; 0"));
        Assert.Null(ImageSigmas.Parse("1, x"));
        Assert.Null(ImageSigmas.Parse("1"));
        Assert.Null(ImageSigmas.Parse(""));
        Assert.Equal("1,0.5,0", ImageSigmas.Format(new[] { 1.0, 0.5, 0.0 }));
    }
}

public class ImageInputTests
{
    [Theory]
    [InlineData(1, false, 1536)]
    [InlineData(1, true, 2048)]
    [InlineData(2, false, 1024)]
    [InlineData(3, true, 1024)]
    [InlineData(4, false, 768)]
    [InlineData(10, false, 768)]
    public void Ref_Limit_By_Count(int count, bool native, int expected) => Assert.Equal(expected, ImageInputPolicy.RefMaxSide(count, native));

    [Fact]
    public void Fit_Within_Keeps_Aspect_And_Multiple()
    {
        var (w, h, changed) = ImageInputPolicy.FitWithin(4000, 3000, 1536, 32);
        Assert.True(changed);
        Assert.Equal((1536, 1152), (w, h));
        Assert.Equal((800, 600, false), ImageInputPolicy.FitWithin(800, 600, 1536));
        var (w2, h2, _) = ImageInputPolicy.FitWithin(1000, 3000, 1024, 32);
        Assert.True(h2 <= 1024 && w2 % 32 == 0 && h2 % 32 == 0);
    }

    [Fact]
    public void Size_From_Source_At_1K_And_2K()
    {
        var (w, h) = ImageInputPolicy.SizeForSource(4000, 3000, 1024, 32);
        Assert.Equal(0, w % 32);
        Assert.Equal(0, h % 32);
        Assert.True(w > h);
        Assert.InRange(w * h, 1024 * 1024 * 0.93, 1024 * 1024 * 1.07);
        var (w2, _) = ImageInputPolicy.SizeForSource(4000, 3000, 2048, 32);
        Assert.Equal(w * 2, w2, 32.0);
        Assert.Equal(64, ImageInputPolicy.AlignDown(70, 32));
        Assert.Equal(32, ImageInputPolicy.Align(1, 32));
    }

    [Fact]
    public void Vae_Tiling_By_Input_Or_Output()
    {
        Assert.False(ImageInputPolicy.NeedsVaeTiling(1024, 1024, new[] { (1024, 768) }));
        Assert.True(ImageInputPolicy.NeedsVaeTiling(1536, 1024, Array.Empty<(int, int)>()));
        Assert.True(ImageInputPolicy.NeedsVaeTiling(1024, 1024, new[] { (4000, 3000) }));
    }

    [Fact]
    public void Placement_Crop_And_Pad()
    {
        var (src, dst) = ImageInputPolicy.Placement(2000, 1000, 1000, 1000, ImageFit.CropResize);
        Assert.Equal(new PixelRect(500, 0, 1000, 1000), src);
        Assert.Equal(new PixelRect(0, 0, 1000, 1000), dst);
        (src, dst) = ImageInputPolicy.Placement(2000, 1000, 1000, 1000, ImageFit.FitPad);
        Assert.Equal(new PixelRect(0, 0, 2000, 1000), src);
        Assert.Equal(new PixelRect(0, 250, 1000, 500), dst);
        (src, dst) = ImageInputPolicy.Placement(2000, 1000, 1000, 1000, ImageFit.Stretch);
        Assert.Equal(new PixelRect(0, 0, 1000, 1000), dst);
    }

    [Fact]
    public void Slow_Step_Per_Megapixel()
    {
        Assert.False(ImageInputPolicy.IsSlowStep(2.5, 1024, 1024));
        Assert.True(ImageInputPolicy.IsSlowStep(14, 1024, 1024));
        Assert.False(ImageInputPolicy.IsSlowStep(14, 2048, 2048)); // 4 Мп — 3,5 с на мегапиксель
    }

    [Fact]
    public void Prompt_Image_Tags()
    {
        Assert.Equal(new[] { "1", "style" }, PromptImageTags.Ids("put <image:1> into <image:style>, keep <image:1>"));
        Assert.Empty(PromptImageTags.Ids(@"literal \<image:1> text"));
        Assert.Equal(new[] { "2" }, PromptImageTags.Ids(@"two slashes \\<image:2>"));
        Assert.Equal("<image:3>", PromptImageTags.Tag("3"));
    }

    [Fact]
    public void Transparent_Prompt_Wrap_Is_Idempotent()
    {
        var w = TransparentPrompt.Wrap("a red apple");
        Assert.Equal(TransparentPrompt.Prefix + " a red apple. " + TransparentPrompt.Suffix, w);
        Assert.Equal(w, TransparentPrompt.Wrap(w));
    }

    [Fact]
    public void Param_Checks_For_Turbo_And_Edit()
    {
        var turbo = ImagePresets.Get(ImageFamily.QwenImage21Turbo);
        var p = new ImageGenParams { Mode = ImageMode.Edit, Cfg = 4, CacheMode = "easycache", Steps = 6, Sigmas = turbo.SigmasText!, Width = 1024, Height = 1024 };
        var w = ImageParamChecks.Check(turbo, p, visionMissing: true);
        Assert.Contains(ImageParamWarning.DistilledCfg, w);
        Assert.Contains(ImageParamWarning.DistilledStepCache, w);
        Assert.Contains(ImageParamWarning.SigmasOverrideSteps, w);
        Assert.Contains(ImageParamWarning.EditBelowNative, w);
        Assert.Contains(ImageParamWarning.VisionMissing, w);
        var ok = new ImageGenParams { Mode = ImageMode.Edit, Cfg = 1, Steps = 8, Sigmas = turbo.SigmasText!, Width = 2048, Height = 1536 };
        Assert.Empty(ImageParamChecks.Check(turbo, ok));
        Assert.Empty(ImageParamChecks.Check(ImagePresets.Get(ImageFamily.Sdxl), new ImageGenParams { Cfg = 7, CacheMode = "ucache" }));
    }

    [Fact]
    public void Conditioning_Cache_Lru()
    {
        var c = new ConditioningCacheTracker { Capacity = 2 };
        var k1 = ConditioningCacheTracker.Key("s", "a", "", new[] { new byte[] { 1 } });
        var k2 = ConditioningCacheTracker.Key("s", "a", "", new[] { new byte[] { 2 } });
        var k3 = ConditioningCacheTracker.Key("s", "b", "", Array.Empty<byte[]>());
        Assert.NotEqual(k1, k2);
        Assert.False(c.Touch(k1));
        Assert.True(c.Touch(k1));
        c.Touch(k2);
        c.Touch(k3); // k1 вытеснен
        Assert.False(c.Contains(k1));
        Assert.True(c.Contains(k2));
        c.Clear();
        Assert.False(c.Contains(k3));
    }

    private static byte[] Png(int w, int h)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        void Chunk(string type, byte[] data)
        {
            var len = Enumerable.Reverse(BitConverter.GetBytes(data.Length)).ToArray();
            ms.Write(len, 0, 4);
            var t = Encoding.ASCII.GetBytes(type);
            ms.Write(t, 0, 4);
            ms.Write(data, 0, data.Length);
            var crc = Enumerable.Reverse(BitConverter.GetBytes(PngMeta.Crc32(t.Concat(data)))).ToArray();
            ms.Write(crc, 0, 4);
        }
        var ihdr = Enumerable.Reverse(BitConverter.GetBytes(w)).Concat(Enumerable.Reverse(BitConverter.GetBytes(h))).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
        Chunk("IHDR", ihdr);
        Chunk("IDAT", new byte[] { 1, 2, 3 });
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void Png_Size_And_Text_Roundtrip()
    {
        var png = Png(640, 480);
        Assert.Equal((640, 480), PngMeta.ReadSize(png));
        Assert.Null(PngMeta.ReadText(png));
        var json = "{\"Prompt\":\"кот в шляпе\"}";
        var withText = PngMeta.WithText(png, json);
        Assert.Equal((640, 480), PngMeta.ReadSize(withText));
        var back = PngMeta.ReadText(withText)!;
        Assert.Equal("кот в шляпе", (string?)JObject.Parse(back)["Prompt"]);
        // Повторная запись заменяет, а не добавляет второй чанк
        var twice = PngMeta.WithText(withText, "{\"Prompt\":\"b\"}");
        Assert.Equal("{\"Prompt\":\"b\"}", PngMeta.ReadText(twice));
        Assert.True(twice.Length < withText.Length + 40);
        Assert.Equal(0xAE426082u, PngMeta.Crc32(Encoding.ASCII.GetBytes("IEND"))); // известная CRC пустого IEND
    }

    [Fact]
    public void Jpeg_Size()
    {
        // SOI, APP0 (длина 4), SOF0: длина 17, точность 8, высота 2, ширина 3
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x02, 0x00, 0x03, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal((3, 2), PngMeta.ReadSize(jpeg));
        Assert.Null(PngMeta.ReadSize(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Canny_Finds_Square_Border()
    {
        const int n = 40;
        var px = new byte[n * n * 4];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var o = (y * n + x) * 4;
            var v = x >= 12 && x < 28 && y >= 12 && y < 28 ? (byte)255 : (byte)0;
            px[o] = px[o + 1] = px[o + 2] = v;
            px[o + 3] = 255;
        }
        var edges = Canny.Detect(px, n, n);
        Assert.Equal(n * n, edges.Length);
        Assert.Equal(0, edges[20 * n + 20]); // внутри квадрата
        Assert.Equal(0, edges[2 * n + 2]); // фон
        var border = Enumerable.Range(10, 4).Any(x => edges[20 * n + x] == 255);
        Assert.True(border);
        Assert.InRange(edges.Count(e => e == 255), 40, 300);
    }
}

public class AuthProxyTests
{
    [Fact]
    public void Authorization_Variants()
    {
        KeyValuePair<string, string> H(string k, string v) => new(k, v);
        Assert.True(AuthProxy.IsAuthorized(new[] { H("Authorization", "Bearer k1") }, "k1"));
        Assert.True(AuthProxy.IsAuthorized(new[] { H("x-api-key", "k1") }, "k1"));
        Assert.False(AuthProxy.IsAuthorized(new[] { H("Authorization", "Bearer k2") }, "k1"));
        Assert.False(AuthProxy.IsAuthorized(Array.Empty<KeyValuePair<string, string>>(), "k1"));
        Assert.False(AuthProxy.IsAuthorized(new[] { H("Authorization", "Bearer ") }, ""));
    }

    [Fact]
    public void Rewrites_Request_Head()
    {
        var req = AuthProxy.ParseHead("POST /v1/images/generations HTTP/1.1\r\nHost: example:8081\r\nAuthorization: Bearer k\r\nConnection: keep-alive\r\nContent-Length: 5\r\n\r\n")!;
        var head = AuthProxy.RewriteRequest(req, 4321);
        Assert.StartsWith("POST /v1/images/generations HTTP/1.1\r\nHost: 127.0.0.1:4321\r\n", head);
        Assert.Contains("Content-Length: 5\r\n", head);
        Assert.DoesNotContain("Authorization", head);
        Assert.DoesNotContain("keep-alive", head);
        Assert.EndsWith("Connection: close\r\n\r\n", head);
        Assert.Null(AuthProxy.ParseHead("garbage\r\n\r\n"));
    }

    [Fact]
    public void Adds_Cors_Once()
    {
        var h = AuthProxy.AddCors("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        Assert.Contains("Access-Control-Allow-Origin: *", h);
        Assert.Equal(h, AuthProxy.AddCors(h));
    }

    [Fact]
    public async Task Proxies_Only_With_Key()
    {
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var heads = new List<string>();
        var serverTask = Task.Run(async () =>
        {
            using var c = await upstream.AcceptTcpClientAsync();
            var s = c.GetStream();
            var (head, rest) = await AuthProxy.ReadHeadAsync(s, CancellationToken.None);
            var body = new byte[5];
            var got = rest.Length;
            Array.Copy(rest, body, Math.Min(rest.Length, 5));
            while (got < 5) got += await s.ReadAsync(body, got, 5 - got);
            lock (heads) heads.Add(head + Encoding.ASCII.GetString(body));
            var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"ok\":true}");
            await s.WriteAsync(resp, 0, resp.Length);
        });

        var port = EngineProcess.FreePort();
        using var proxy = new AuthProxy("127.0.0.1", port, upstreamPort, () => "secret");
        Assert.Null(proxy.Start());
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });

        var denied = await http.PostAsync($"http://127.0.0.1:{port}/v1/images/generations", new StringContent("hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("invalid_api_key", await denied.Content.ReadAsStringAsync());

        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/images/generations") { Content = new StringContent("hello") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        var ok = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("{\"ok\":true}", await ok.Content.ReadAsStringAsync());
        Assert.True(ok.Headers.Contains("Access-Control-Allow-Origin"));
        await serverTask;
        upstream.Stop();
        Assert.Contains("hello", heads[0]);
        Assert.DoesNotContain("secret", heads[0]);
    }
}

public class HfTaskTests
{
    [Fact]
    public void Search_Url_By_Task()
    {
        var url = HfClient.SearchUrl("https://huggingface.co/", "flux", HfTask.TextToImage, ggufOnly: false, HfSort.Likes, 20);
        Assert.StartsWith("https://huggingface.co/api/models?sort=likes", url);
        Assert.Contains("pipeline_tag=text-to-image", url);
        Assert.DoesNotContain("filter=gguf", url);
        Assert.Contains("search=flux", url);
        var all = HfClient.SearchUrl("https://hf", null, HfTask.All, ggufOnly: true, HfSort.Downloads, 5);
        Assert.Contains("filter=gguf", all);
        Assert.DoesNotContain("&pipeline_tag=", all);
    }

    [Fact]
    public void Runtime_By_Task()
    {
        Assert.Equal(HfRuntime.StableDiffusion, HfTasks.RuntimeFor(HfTask.ImageToImage));
        Assert.Equal(HfRuntime.Llama, HfTasks.RuntimeFor(HfTask.AnyToAny));
        Assert.Equal(HfRuntime.None, HfTasks.RuntimeFor(HfTask.TextToSpeech));
        Assert.Equal(HfRuntime.StableDiffusion, HfTasks.RuntimeFor("text-to-image"));
        Assert.False(HfTasks.GgufByDefault(HfTask.TextToImage));
    }
}

public class TokenBudgetTests
{
    [Theory]
    [InlineData(4096, "4K")]
    [InlineData(131072, "128K")]
    [InlineData(512, "512")]
    [InlineData(1500, "1.5K")]
    public void Short_Labels(int tokens, string expected) => Assert.Equal(expected, TokenBudget.Short(tokens));

    [Fact]
    public void Warnings()
    {
        Assert.Equal(TokenBudgetWarning.None, TokenBudget.Check(8192, 2048, 32768));
        Assert.Equal(TokenBudgetWarning.AnswerFillsContext, TokenBudget.Check(4096, 4096, null));
        Assert.Equal(TokenBudgetWarning.AnswerTakesMostOfContext, TokenBudget.Check(8192, 6000, null));
        Assert.Equal(TokenBudgetWarning.ContextAboveTrained, TokenBudget.Check(65536, -1, 32768));
        Assert.Equal(TokenBudgetWarning.None, TokenBudget.Check(0, 99999, null));
    }

    [Fact]
    public void Approximations()
    {
        Assert.Equal(768, TokenBudget.ApproxWords(1024));
        Assert.Equal(1.5, TokenBudget.ApproxPages(1024));
        Assert.Equal(0, TokenBudget.ApproxWords(-1));
    }
}
