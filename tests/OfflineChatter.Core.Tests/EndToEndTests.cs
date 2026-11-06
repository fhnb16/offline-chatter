using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace OfflineChatter.Core.Tests;

/// <summary>
/// Полный путь: релиз llama.cpp → установка → модель по умолчанию с HF → профиль → llama-server → ответ модели.
/// Запуск: OC_E2E=1, OC_E2E_DIR=папка для кэша (движок и ~750 МБ модели скачиваются один раз).
/// </summary>
public class EndToEndTests
{
    private readonly ITestOutputHelper _out;

    public EndToEndTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Install_Download_Start_Chat()
    {
        if (Environment.GetEnvironmentVariable("OC_E2E") != "1") return;
        var root = Environment.GetEnvironmentVariable("OC_E2E_DIR") ?? Path.Combine(Path.GetTempPath(), "oc-e2e");
        var paths = new AppPaths(root, true);
        paths.EnsureCreated();

        var net = new NetworkSettings { ProxyMode = ProxyMode.None };
        using var http = new HttpService(net, new TrustedRoots(), "e2e");
        var downloads = new DownloadManager(http, net);
        var releases = new GitHubReleases(http);
        var settings = new EngineSettings();
        using var engines = new EngineManager(paths, settings, () => { });

        // 1. Движок (CPU, закреплённый тег)
        var engine = engines.Registry.List().FirstOrDefault(e => e.Manifest.Tag == GitHubReleases.KnownGoodTag && e.Manifest.Backend == EngineBackend.Cpu);
        if (engine == null)
        {
            var rel = await releases.GetByTagAsync(GitHubReleases.KnownGoodTag, CancellationToken.None);
            var variant = AssetMatcher.WindowsVariants(rel).First(v => v.Backend == EngineBackend.Cpu && v.Arch == CpuArch.X64);
            var installer = new EngineInstaller(paths, downloads, releases);
            var (installed, smoke) = await installer.InstallAsync(rel, variant, null, CancellationToken.None);
            Assert.True(smoke.Ok, smoke.Output);
            engine = installed;
        }
        engines.SetActive(engine);
        _out.WriteLine("Engine: " + engine.DisplayName);

        // 2. Модель по умолчанию + mmproj
        var hf = new HfClient(http, () => null);
        var files = await hf.GetFilesAsync(HfClient.DefaultModelRepo, CancellationToken.None);
        var quant = files.Quants.First();
        var dir = Path.Combine(paths.Models, "NotCherry", "default");
        DownloadJob Job(HfFile f) => downloads.Enqueue(new DownloadJob(hf.FileUrl(HfClient.DefaultModelRepo, f.Path), Path.Combine(dir, Path.GetFileName(f.Path)), f.Path)
        { ExpectedSize = f.Size, ExpectedSha256 = f.Sha256 });
        var modelPath = Path.Combine(dir, Path.GetFileName(quant.Primary.Path));
        var mmPath = Path.Combine(dir, Path.GetFileName(files.PreferredMmproj!.Path));
        if (!File.Exists(modelPath)) Assert.True(await Job(quant.Primary).WhenFinished, "model download failed");
        if (!File.Exists(mmPath)) Assert.True(await Job(files.PreferredMmproj!).WhenFinished, "mmproj download failed");

        var model = ModelRegistry.Describe(modelPath, ModelSource.HuggingFace, HfClient.DefaultModelRepo);
        Assert.True(model.HasVision);
        _out.WriteLine($"Model: {model.DisplayName}, {model.SizeBytes / 1048576} MB, arch {model.Architecture}");

        // 3. Профиль → аргументы
        var hw = await engines.GetHardwareAsync();
        var flags = await engines.GetFlagsAsync(engine);
        Assert.NotNull(flags);
        var info = GgufReader.ReadFile(modelPath);
        var resolved = ProfileResolver.Resolve(LaunchProfile.CreateBuiltIns().First(p => p.Id == "fast"), model, info, hw, engine, flags);
        _out.WriteLine("Notes: " + string.Join("; ", resolved.Notes) + "; verdict " + resolved.Verdict);

        // 4. Сервер
        try
        {
            await engines.StartAsync(resolved.Options, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            _out.WriteLine(engines.Server.CommandLine);
            Assert.True(engines.Server.State == ServerState.Ready, engines.Server.LastError + Environment.NewLine + engines.Server.Tail);
            _out.WriteLine($"Ready in {engines.Server.LoadTime}");

            // 5. Ответ модели
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engines.Server.ApiKey);
            var body = new JObject
            {
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "Say hello in one word." }),
                ["max_tokens"] = 256,
                ["temperature"] = 0,
                ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },
            };
            var resp = await client.PostAsync(engines.Server.BaseUrl + "/v1/chat/completions", new StringContent(body.ToString(), Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, text);
            var json = JObject.Parse(text);
            var message = json["choices"]![0]!["message"]!;
            var answer = (string?)message["content"];
            _out.WriteLine("Message: " + message.ToString(Newtonsoft.Json.Formatting.None));
            _out.WriteLine("Answer: " + answer);
            _out.WriteLine("Timings: " + json["timings"]);
            Assert.False(string.IsNullOrWhiteSpace(answer));

            // Без ключа сервер должен отказать
            using var anon = new HttpClient(new HttpClientHandler { UseProxy = false });
            var denied = await anon.PostAsync(engines.Server.BaseUrl + "/v1/chat/completions", new StringContent(body.ToString(), Encoding.UTF8, "application/json"));
            Assert.Equal(401, (int)denied.StatusCode);
        }
        finally
        {
            engines.Server.Stop();
        }
        Assert.Equal(ServerState.Stopped, engines.Server.State);
    }
}
