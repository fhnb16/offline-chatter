using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Mcp;
using Xunit;

namespace OfflineChatter.Core.Tests;

public sealed class McpStdioTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void Resolves_Executables_And_Batch_Files_With_Their_Own_Quoting()
    {
        using var tmp = new TempDir();
        var bin = Directory.CreateDirectory(tmp.Combine("MCP tools")).FullName;
        var uv = Path.Combine(bin, "uv.exe");
        var npx = Path.Combine(bin, "npx.cmd");
        File.WriteAllText(uv, "");
        File.WriteAllText(npx, "");
        var cfg = new McpServerConfig
        {
            Command = "uv",
            Args = new List<string> { "run", "--script", "server files\\blender.py" },
            Cwd = tmp.Path,
            Env = new Dictionary<string, string> { ["PATH"] = bin },
        };

        var direct = StdioTransport.CreateStartInfo(cfg);
        Assert.Equal(uv, direct.FileName);
        Assert.Equal("run --script \"server files\\blender.py\"", direct.Arguments);
        Assert.Equal(tmp.Path, direct.WorkingDirectory);

        cfg.Command = npx;
        cfg.Args = new List<string> { "-y", "package", "C:\\render & preview" };
        var batch = StdioTransport.CreateStartInfo(cfg);
        Assert.Equal("cmd.exe", Path.GetFileName(batch.FileName));
        Assert.Contains("\"C:\\render & preview\"", batch.Arguments);
        Assert.Contains("\"" + npx + "\"", batch.Arguments);
    }

    [Fact]
    public async Task Exchanges_Utf8_With_A_Process_And_Loads_A_Relative_Render()
    {
        using var tmp = new TempDir();
        const string render = "тест рендера.png";
        File.WriteAllBytes(tmp.Combine(render), Convert.FromBase64String(Png));
        var script = """
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            while ($null -ne ($line = [Console]::ReadLine())) {
                $m = ConvertFrom-Json $line
                if ($null -eq $m.id) { continue }
                $result = switch ($m.method) {
                    'initialize' { @{ protocolVersion = $m.params.protocolVersion; serverInfo = @{ name = 'render-test'; version = '1' }; capabilities = @{ tools = @{} } } }
                    'tools/list' { @{ tools = @(@{ name = 'render'; inputSchema = @{ type = 'object' } }) } }
                    'tools/call' { @{ content = @(@{ type = 'text'; text = $m.params.arguments.file }) } }
                }
                [Console]::Error.WriteLine('render-test diagnostic')
                [Console]::WriteLine((ConvertTo-Json -Compress -Depth 10 @{ jsonrpc = '2.0'; id = $m.id; result = $result }))
            }
            """;
        var cfg = new McpServerConfig
        {
            Name = "render-test",
            Command = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Args = new List<string> { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
            Cwd = tmp.Path,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new McpClient(cfg.Name, new StdioTransport(cfg));

        await client.ConnectAsync(cts.Token);
        Assert.Equal("render", Assert.Single(client.Tools).Name);
        var result = await client.CallToolAsync("render", new JObject { ["file"] = render }, cts.Token);
        Assert.Equal(render, result.Text);
        var image = Assert.Single(result.Images);
        Assert.Equal(AttachmentKind.Image, image.Kind);
        Assert.Equal(render, image.Name);
        Assert.Equal(Png, image.Data);
    }

    [Fact]
    public async Task Reads_Mcp_Images_And_Only_Resolves_File_Hints_For_Stdio()
    {
        using var tmp = new TempDir();
        var render = tmp.Combine("рендер с пробелами.png");
        var second = tmp.Combine("viewport.png");
        File.WriteAllBytes(render, Convert.FromBase64String(Png));
        File.WriteAllBytes(second, Convert.FromBase64String(Png));
        var payload = new JObject
        {
            ["content"] = new JArray(
                new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Png },
                new JObject { ["type"] = "resource", ["resource"] = new JObject { ["mimeType"] = "image/png", ["uri"] = "render://frame", ["blob"] = Png } },
                new JObject { ["type"] = "text", ["text"] = "Render completed: " + render },
                new JObject { ["type"] = "resource_link", ["uri"] = new Uri(second).AbsoluteUri },
                new JObject { ["type"] = "resource_link", ["uri"] = "https://example.com/image.png" }),
            ["structuredContent"] = new JObject { ["filepath"] = render },
        };
        var local = await McpResultReader.ReadAsync(payload, true, tmp.Path, CancellationToken.None);
        Assert.Equal(new[] { "image-1.png", "image-2.png", Path.GetFileName(render), Path.GetFileName(second) }, local.Images.Select(a => a.Name));
        Assert.All(local.Images, image => Assert.Equal(Png, image.Data));
        Assert.DoesNotContain(Png, local.Text);

        var remote = await McpResultReader.ReadAsync(payload, false, null, CancellationToken.None);
        Assert.Equal(2, remote.Images.Count);
        Assert.Equal(local.Text, remote.Text);

        var call = new ToolCall { Images = local.Images };
        var persisted = JsonConvert.DeserializeObject<ToolCall>(JsonConvert.SerializeObject(call.Snapshot()))!;
        Assert.Equal(local.Images.Select(image => image.Name), persisted.Images!.Select(image => image.Name));
    }
}
