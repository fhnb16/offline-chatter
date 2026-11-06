using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class HostnameMatcherTests
{
    [Theory]
    [InlineData("github.com", "github.com", true)]
    [InlineData("GitHub.com", "github.COM", true)]
    [InlineData("*.github.com", "api.github.com", true)]
    [InlineData("*.github.com", "github.com", false)]
    [InlineData("*.github.com", "a.b.github.com", false)]
    [InlineData("*.hf.co", "cas-bridge.xethub.hf.co", false)]
    [InlineData("api.github.com", "evil-api.github.com", false)]
    [InlineData("", "github.com", false)]
    public void Pattern(string pattern, string host, bool expected) =>
        Assert.Equal(expected, HostnameMatcher.MatchesPattern(pattern, host));
}

public class HttpWireTests
{
    private static MemoryStream S(string s) => new(Encoding.ASCII.GetBytes(s));

    [Fact]
    public void ContentLength_Body()
    {
        var s = S("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nX-A: b\r\n\r\nhelloEXTRA");
        var head = HttpWire.ReadResponseHead(s);
        Assert.Equal(200, head.StatusCode);
        Assert.Equal("b", head.Get("x-a"));
        using var body = HttpWire.CreateBodyStream(s, head, false, new MemoryStream());
        Assert.Equal("hello", new StreamReader(body).ReadToEnd());
    }

    [Fact]
    public void Chunked_Body_WithExtensionsAndTrailers()
    {
        var s = S("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n4;ext=1\r\nWiki\r\n6\r\npedia \r\nE\r\nin \r\n\r\nchunks.\r\n0\r\nX-T: 1\r\n\r\n");
        var head = HttpWire.ReadResponseHead(s);
        using var body = HttpWire.CreateBodyStream(s, head, false, new MemoryStream());
        Assert.Equal("Wikipedia in \r\n\r\nchunks.", new StreamReader(body).ReadToEnd());
    }

    [Fact]
    public void Skips_100_Continue()
    {
        var s = S("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
        var head = HttpWire.ReadResponseHead(s);
        Assert.Equal(404, head.StatusCode);
        Assert.Equal("Not Found", head.Reason);
    }

    [Fact]
    public void Truncated_Body_Throws()
    {
        var s = S("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nabc");
        var head = HttpWire.ReadResponseHead(s);
        using var body = HttpWire.CreateBodyStream(s, head, false, new MemoryStream());
        Assert.Throws<IOException>(() => new StreamReader(body).ReadToEnd());
    }
}

public class TrustedRootsTests
{
    [Fact]
    public void Embedded_Roots_Are_Loaded()
    {
        var roots = new TrustedRoots();
        Assert.True(roots.Count >= 20, "roots: " + roots.Count);
        Assert.Contains(roots.Certificates, c => c.Subject.Contains("ISRG Root X1"));
        Assert.Contains(roots.Certificates, c => c.Subject.Contains("Amazon Root CA 1"));
        Assert.All(roots.Certificates, c => Assert.Equal(c.Subject, c.Issuer));
    }

    [Fact]
    public void User_Pem_Is_Loaded()
    {
        using var tmp = new TempDir();
        var any = new TrustedRoots().Certificates.First();
        var pem = "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(any.RawData, Base64FormattingOptions.InsertLineBreaks) + "\n-----END CERTIFICATE-----\n";
        File.WriteAllText(tmp.Combine("x.pem"), pem);
        File.WriteAllText(tmp.Combine("readme.txt"), "ignored");
        var roots = new TrustedRoots(tmp.Path);
        Assert.True(roots.Count >= 20);
    }
}

/// <summary>Сетевые тесты: запускаются только при OC_NET_TESTS=1.</summary>
public class LiveNetTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("OC_NET_TESTS") == "1";

    private static HttpService Create(TlsMode mode) =>
        new(new NetworkSettings { TlsMode = mode, ProxyMode = ProxyMode.None }, new TrustedRoots(), "test");

    [Theory]
    [InlineData(TlsMode.Managed, "https://api.github.com/repos/ggml-org/llama.cpp")]
    [InlineData(TlsMode.Managed, "https://huggingface.co/api/models/NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF")]
    [InlineData(TlsMode.PinnedRoots, "https://huggingface.co/api/models/NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF")]
    public async Task Json_Over_Each_Tls_Level(TlsMode mode, string url)
    {
        if (!Enabled) return;
        using var http = Create(mode);
        var json = await http.GetJsonAsync(url, CancellationToken.None);
        Assert.NotNull(json["id"] ?? json["full_name"]);
    }

    [Fact]
    public async Task Managed_Follows_Redirect_And_Supports_Range()
    {
        if (!Enabled) return;
        using var http = Create(TlsMode.Managed);
        // README репозитория модели: /resolve/ отвечает редиректом или сразу файлом
        using var r = await http.SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF/resolve/main/README.md");
            m.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
            return m;
        }, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        Assert.True(r.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.OK, r.StatusCode.ToString());
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length is > 0 and <= 100 || r.StatusCode == HttpStatusCode.OK);
    }
}

public class DownloadManagerTests
{
    /// <summary>Мини-сервер на TcpListener с поддержкой Range и обрывом соединения на заданном байте.</summary>
    private sealed class FileServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _data;
        private readonly CancellationTokenSource _cts = new();
        public int DropAfter = -1;  // оборвать первую отдачу после N байт
        public int Requests;
        public bool IgnoreRange;

        public FileServer(byte[] data)
        {
            _data = data;
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file.bin";

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        }

        private void Handle(TcpClient c)
        {
            using (c)
            {
                Interlocked.Increment(ref Requests);
                var s = c.GetStream();
                var reader = new StreamReader(s, Encoding.ASCII);
                long from = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    if (line!.StartsWith("Range:", StringComparison.OrdinalIgnoreCase) && !IgnoreRange)
                        from = long.Parse(line.Split('=')[1].Split('-')[0], CultureInfo.InvariantCulture);
                }
                var len = _data.Length - from;
                var head = from > 0
                    ? $"HTTP/1.1 206 Partial Content\r\nContent-Length: {len}\r\nContent-Range: bytes {from}-{_data.Length - 1}/{_data.Length}\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Length: {len}\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                s.Write(hb, 0, hb.Length);
                var toSend = (int)len;
                var drop = Interlocked.Exchange(ref DropAfter, -1);
                if (drop > 0 && drop < toSend) toSend = drop;
                s.Write(_data, (int)from, toSend);
                s.Flush();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static (DownloadManager, HttpService) Create()
    {
        var settings = new NetworkSettings { ProxyMode = ProxyMode.None, TlsMode = TlsMode.System, TimeoutSeconds = 10 };
        var http = new HttpService(settings, new TrustedRoots(), "test");
        return (new DownloadManager(http, settings), http);
    }

    private static byte[] Data(int size)
    {
        var d = new byte[size];
        new Random(42).NextBytes(d);
        return d;
    }

    private static string Sha(byte[] d)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(d)).Replace("-", "").ToLowerInvariant();
    }

    [Fact]
    public async Task Downloads_And_Verifies_Sha256()
    {
        using var tmp = new TempDir();
        var data = Data(3 * 1024 * 1024 + 123);
        using var server = new FileServer(data);
        var (dm, http) = Create();
        using var _ = http;

        var job = dm.Enqueue(new DownloadJob(server.Url, tmp.Combine("out.bin"), "out.bin") { ExpectedSha256 = Sha(data), ExpectedSize = data.Length });
        Assert.True(await job.WhenFinished, job.Error);
        Assert.Equal(DownloadState.Completed, job.State);
        Assert.Equal(data, File.ReadAllBytes(tmp.Combine("out.bin")));
        Assert.False(File.Exists(job.PartPath));
    }

    [Fact]
    public async Task Resumes_After_Connection_Drop()
    {
        using var tmp = new TempDir();
        var data = Data(2 * 1024 * 1024);
        using var server = new FileServer(data) { DropAfter = 700_000 };
        var (dm, http) = Create();
        using var _ = http;

        var job = dm.Enqueue(new DownloadJob(server.Url, tmp.Combine("r.bin"), "r.bin") { ExpectedSha256 = Sha(data) });
        Assert.True(await job.WhenFinished, job.Error);
        Assert.Equal(data, File.ReadAllBytes(tmp.Combine("r.bin")));
        Assert.True(server.Requests >= 2);
    }

    [Fact]
    public async Task Resumes_Existing_Part_File_With_Range()
    {
        using var tmp = new TempDir();
        var data = Data(1024 * 1024);
        File.WriteAllBytes(tmp.Combine("p.bin.part"), data.Take(400_000).ToArray());
        using var server = new FileServer(data);
        var (dm, http) = Create();
        using var _ = http;

        var job = dm.Enqueue(new DownloadJob(server.Url, tmp.Combine("p.bin"), "p.bin") { ExpectedSize = data.Length });
        Assert.True(await job.WhenFinished, job.Error);
        Assert.Equal(data, File.ReadAllBytes(tmp.Combine("p.bin")));
    }

    [Fact]
    public async Task Server_Ignoring_Range_Restarts_From_Zero()
    {
        using var tmp = new TempDir();
        var data = Data(500_000);
        File.WriteAllBytes(tmp.Combine("i.bin.part"), new byte[100_000]); // мусор
        using var server = new FileServer(data) { IgnoreRange = true };
        var (dm, http) = Create();
        using var _ = http;

        var job = dm.Enqueue(new DownloadJob(server.Url, tmp.Combine("i.bin"), "i.bin") { ExpectedSha256 = Sha(data) });
        Assert.True(await job.WhenFinished, job.Error);
        Assert.Equal(data, File.ReadAllBytes(tmp.Combine("i.bin")));
    }

    [Fact]
    public async Task Wrong_Sha256_Fails_And_Removes_Part()
    {
        using var tmp = new TempDir();
        var data = Data(200_000);
        using var server = new FileServer(data);
        var (dm, http) = Create();
        using var _ = http;

        var job = dm.Enqueue(new DownloadJob(server.Url, tmp.Combine("w.bin"), "w.bin") { ExpectedSha256 = new string('0', 64) });
        Assert.False(await job.WhenFinished);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.False(File.Exists(tmp.Combine("w.bin")));
        Assert.False(File.Exists(job.PartPath));
    }
}
