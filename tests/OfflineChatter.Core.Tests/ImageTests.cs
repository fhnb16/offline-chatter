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
    public async Task Falls_Back_To_OpenAi_When_Sdapi_Missing()
    {
        var b64 = Convert.ToBase64String(new byte[] { 9 });
        var seen = new List<string>();
        var handler = new StubHandler(req =>
        {
            seen.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri.AbsolutePath == "/sdapi/v1/txt2img"
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"data\":[{{\"b64_json\":\"{b64}\"}}]}}") };
        });
        var client = new ImageClient(new HttpClient(handler), "http://127.0.0.1:1");
        var r = await client.GenerateAsync(new ImageRequest { Params = new ImageGenParams { Prompt = "a" } }, CancellationToken.None);
        Assert.Equal("/v1/images/generations", r.Endpoint);
        Assert.Equal(new[] { "/sdapi/v1/txt2img", "/v1/images/generations" }, seen);
        Assert.Equal(new byte[] { 9 }, r.Images[0]);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_f(request));
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
