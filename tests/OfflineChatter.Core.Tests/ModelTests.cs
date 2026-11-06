using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class GgufReaderTests
{
    /// <summary>Пишет минимальный GGUF v3 с метаданными и большим массивом строк (как словарь токенизатора).</summary>
    internal static byte[] MakeGguf(string arch = "llama", int blocks = 4, int emb = 256, int heads = 8, int kvHeads = 2, int ctx = 4096)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)b.Length);
            w.Write(b);
        }
        void KvStr(string k, string v) { Str(k); w.Write(8u); Str(v); }
        void KvU32(string k, uint v) { Str(k); w.Write(4u); w.Write(v); }

        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(0UL);   // tensors
        w.Write(9UL);   // kv count
        KvStr("general.architecture", arch);
        KvStr("general.name", "Test Model");
        KvStr("general.size_label", "0.8B");
        KvU32(arch + ".block_count", (uint)blocks);
        KvU32(arch + ".embedding_length", (uint)emb);
        KvU32(arch + ".attention.head_count", (uint)heads);
        KvU32(arch + ".attention.head_count_kv", (uint)kvHeads);
        KvU32(arch + ".context_length", (uint)ctx);
        // массив строк
        Str("tokenizer.ggml.tokens");
        w.Write(9u);
        w.Write(8u);
        w.Write(5000UL);
        for (var i = 0; i < 5000; i++) Str("tok" + i);
        return ms.ToArray();
    }

    [Fact]
    public void Reads_Metadata_And_Skips_Token_Strings()
    {
        var info = GgufReader.Read(new MemoryStream(MakeGguf()));
        Assert.Equal(3u, info.Version);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal("Test Model", info.Name);
        Assert.Equal(4, info.BlockCount);
        Assert.Equal(2, info.HeadCountKv);
        Assert.Equal(4096, info.ContextLength);
        var tokens = Assert.IsType<GgufArray>(info.Metadata["tokenizer.ggml.tokens"]);
        Assert.Equal(5000UL, tokens.Count);
        Assert.False(info.IsMmproj);
    }

    [Fact]
    public void Rejects_Non_Gguf() =>
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("NOPE0000000000000000"))));

    [Fact]
    public async Task Remote_Header_Of_Default_Model()
    {
        if (Environment.GetEnvironmentVariable("OC_NET_TESTS") != "1") return;
        using var http = new HttpService(new NetworkSettings { ProxyMode = ProxyMode.None }, new TrustedRoots(), "test");
        var hf = new HfClient(http, () => null);
        var files = await hf.GetFilesAsync(HfClient.DefaultModelRepo, CancellationToken.None);
        var q = Assert.Single(files.Quants);
        Assert.Equal("Q4_K_M", q.Quant!.Name);
        Assert.NotNull(files.PreferredMmproj);
        var info = await hf.ReadRemoteGgufAsync(HfClient.DefaultModelRepo, q.Primary.Path, CancellationToken.None);
        Assert.Equal("qwen35", info.Architecture);
        Assert.True(info.BlockCount > 0);
        var mm = await hf.ReadRemoteGgufAsync(HfClient.DefaultModelRepo, files.PreferredMmproj!.Path, CancellationToken.None);
        Assert.True(mm.IsMmproj);

        var found = await hf.SearchAsync("qwen", visionOnly: true, HfSort.Downloads, 5, CancellationToken.None);
        Assert.NotEmpty(found);
    }
}

public class QuantTests
{
    [Theory]
    [InlineData("Qwen3.5-0.8B-Weight-Space-Programming-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("Qwen3-8B-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("gemma-3-4b-it-IQ3_XXS.gguf", "IQ3_XXS")]
    [InlineData("model-q8_0.gguf", "Q8_0")]
    [InlineData("Llama-3.2-1B-Instruct-f16.gguf", "F16")]
    [InlineData("Qwen2.5-7B-Instruct-BF16-00001-of-00002.gguf", "BF16")]
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    public void Detects_Quant(string file, string expected) => Assert.Equal(expected, QuantCatalog.FromFileName(file)!.Name);

    [Fact]
    public void Unknown_Quant_Is_Null() => Assert.Null(QuantCatalog.FromFileName("readme.gguf"));

    [Fact]
    public void Quality_Order()
    {
        int R(string f) => QuantCatalog.FromFileName(f)!.QualityRank;
        Assert.True(R("a-IQ2_XS.gguf") < R("a-Q3_K_M.gguf"));
        Assert.True(R("a-Q3_K_M.gguf") < R("a-Q4_K_M.gguf"));
        Assert.True(R("a-Q4_K_M.gguf") < R("a-Q6_K.gguf"));
        Assert.True(R("a-Q6_K.gguf") < R("a-Q8_0.gguf"));
        Assert.True(R("a-Q8_0.gguf") < R("a-F16.gguf"));
    }

    [Fact]
    public void Groups_Shards_And_Mmproj()
    {
        var files = new[]
        {
            new HfFile("Q8_0/model-Q8_0-00002-of-00002.gguf", 10, null),
            new HfFile("Q8_0/model-Q8_0-00001-of-00002.gguf", 10, null),
            new HfFile("model-Q4_K_M.gguf", 8, null),
            new HfFile("mmproj-model-f16.gguf", 2, null),
            new HfFile("mmproj-model-Q8_0.gguf", 1, null),
        };
        var g = HfClient.GroupFiles(files);
        Assert.Equal(2, g.Quants.Count);
        Assert.Equal("Q4_K_M", g.Quants[0].Quant!.Name);
        var q8 = g.Quants[1];
        Assert.True(q8.IsSharded);
        Assert.Equal(20, q8.Size);
        Assert.EndsWith("00001-of-00002.gguf", q8.Primary.Path);
        Assert.Equal("mmproj-model-f16.gguf", g.PreferredMmproj!.Path);
    }

    [Theory]
    [InlineData("0.8B", 0.8e9)]
    [InlineData("7B", 7e9)]
    [InlineData("8x7B", 56e9)]
    [InlineData("135M", 135e6)]
    public void Params_From_Label(string label, double expected) => Assert.Equal(expected, QuantCatalog.ParamsFromLabel(label)!.Value, 3);
}

public class ModelRegistryTests
{
    [Fact]
    public void Describe_External_File_Finds_Mmproj_And_Shards_Without_Copying()
    {
        using var tmp = new TempDir();
        var dir = tmp.Combine("D-drive", "models");
        Directory.CreateDirectory(dir);
        var gguf = GgufReaderTests.MakeGguf();
        File.WriteAllBytes(Path.Combine(dir, "my-Q5_K_M-00001-of-00002.gguf"), gguf);
        File.WriteAllBytes(Path.Combine(dir, "my-Q5_K_M-00002-of-00002.gguf"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "mmproj-my-Q8_0.gguf"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir, "mmproj-my-F16.gguf"), new byte[10]);

        var m = ModelRegistry.Describe(Path.Combine(dir, "my-Q5_K_M-00002-of-00002.gguf"), ModelSource.LocalFile);
        Assert.EndsWith("00001-of-00002.gguf", m.Path);
        Assert.Equal(gguf.Length + 100, m.SizeBytes);
        Assert.EndsWith("mmproj-my-F16.gguf", m.MmprojPath);
        Assert.Equal("Q5_K_M", m.Quant);
        Assert.Equal("llama", m.Architecture);
        Assert.Equal(800_000_000, m.Parameters);

        var reg = new ModelRegistry(tmp.Combine("models.json"));
        reg.AddOrUpdate(m);
        reg.AddOrUpdate(ModelRegistry.Describe(m.Path, ModelSource.Scanned)); // дубль не создаётся
        Assert.Single(new ModelRegistry(tmp.Combine("models.json")).Models);
        Assert.True(File.Exists(Path.Combine(dir, "my-Q5_K_M-00001-of-00002.gguf")));
    }

    [Fact]
    public void Scanner_Skips_Mmproj_And_Extra_Shards()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("a", "b"));
        File.WriteAllText(tmp.Combine("a", "x-Q4_K_M.gguf"), "");
        File.WriteAllText(tmp.Combine("a", "mmproj-x.gguf"), "");
        File.WriteAllText(tmp.Combine("a", "b", "y-00001-of-00002.gguf"), "");
        File.WriteAllText(tmp.Combine("a", "b", "y-00002-of-00002.gguf"), "");
        var found = LocalScanner.Scan(new[] { tmp.Path, tmp.Combine("missing") });
        Assert.Equal(2, found.Count);
    }
}

public class MemoryEstimatorTests
{
    [Fact]
    public void Kv_Cache_Formula()
    {
        var info = GgufReader.Read(new MemoryStream(GgufReaderTests.MakeGguf(blocks: 32, emb: 4096, heads: 32, kvHeads: 8, ctx: 131072)));
        // 2 * 32 слоя * 8192 ctx * 8 голов * 128 dim * 2 байта = 1 ГиБ
        var e = MemoryEstimator.Estimate(info, 4L << 30, 8192);
        Assert.Equal(1L << 30, e.KvCache);
        var q8 = MemoryEstimator.Estimate(info, 4L << 30, 8192, "q8_0", "q8_0");
        Assert.True(q8.KvCache < e.KvCache * 0.6);
    }

    [Fact]
    public void Suggestions_Respect_Memory()
    {
        var info = GgufReader.Read(new MemoryStream(GgufReaderTests.MakeGguf(blocks: 32, emb: 4096, heads: 32, kvHeads: 8, ctx: 131072)));
        var ctx = MemoryEstimator.SuggestContext(info, 4L << 30, 8L << 30);
        Assert.InRange(ctx, 4096, 32768);
        Assert.Equal(-1, MemoryEstimator.SuggestGpuLayers(info, 4L << 30, 4096, 12L << 30));
        Assert.InRange(MemoryEstimator.SuggestGpuLayers(info, 4L << 30, 4096, 3L << 30), 1, 32);
        Assert.Equal(0, MemoryEstimator.SuggestGpuLayers(info, 4L << 30, 4096, 0));
        Assert.Equal(FitVerdict.DoesNotFit, MemoryEstimator.Verdict(10, 5));
    }
}
