using System;
using System.IO;

namespace OfflineChatter.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(Path, System.IO.Path.Combine(parts));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
