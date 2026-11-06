using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OfflineChatter.Core.Engine;

public enum EngineHealth
{
    Ok,
    MissingFile,
    MissingDll,
    OsTooOld,
    IllegalInstruction,
    Timeout,
    Crashed,
}

public sealed record SmokeResult(EngineHealth Health, int? ExitCode, string? VersionText, int? Build, string Output)
{
    public bool Ok => Health == EngineHealth.Ok;
}

public sealed record RuntimeStatus(bool Ucrt, bool VcRuntime)
{
    public bool Ok => Ucrt && VcRuntime;
}

/// <summary>
/// Проверка, что движок запустится на этой системе: CRT, запуск llama-server --version и диагностика по коду выхода.
/// </summary>
public static class CompatChecker
{
    public const string VcRedistUrlX64 = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    public const string VcRedistUrlArm64 = "https://aka.ms/vs/17/release/vc_redist.arm64.exe";
    public const string UcrtKbUrl = "https://support.microsoft.com/kb/2999226";

    private const int StatusDllNotFound = unchecked((int)0xC0000135);
    private const int StatusEntryPointNotFound = unchecked((int)0xC0000139);
    private const int StatusIllegalInstruction = unchecked((int)0xC000001D);
    private const int StatusInvalidImage = unchecked((int)0xC000007B);

    private static readonly Regex VersionRx = new(@"build\s+(?<b>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>UCRT и VC++ 2015–2022: в System32 или рядом с движком (app-local).</summary>
    public static RuntimeStatus CheckRuntime(string? engineDir)
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        bool Has(string dll) => File.Exists(Path.Combine(sys, dll)) || (engineDir != null && File.Exists(Path.Combine(engineDir, dll)));
        return new RuntimeStatus(Has("ucrtbase.dll"), Has("vcruntime140.dll") && Has("msvcp140.dll"));
    }

    public static async Task<SmokeResult> SmokeTestAsync(string serverExe, TimeSpan? timeout = null)
    {
        if (!File.Exists(serverExe)) return new SmokeResult(EngineHealth.MissingFile, null, null, null, serverExe);

        var psi = new ProcessStartInfo(serverExe, "--version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Системные диалоги «не найдена DLL» не должны всплывать во время проверки
        using var _ = ErrorModeScope.Silent();
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SmokeResult(ex.NativeErrorCode == 216 ? EngineHealth.OsTooOld : EngineHealth.Crashed, null, null, null, ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var exited = await Task.Run(() => p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(20)).TotalMilliseconds)).ConfigureAwait(false);
        if (!exited)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return new SmokeResult(EngineHealth.Timeout, null, null, null, output.ToString());
        }
        p.WaitForExit();
        var text = output.ToString();
        var code = p.ExitCode;
        var health = code switch
        {
            0 => EngineHealth.Ok,
            StatusDllNotFound or StatusInvalidImage => EngineHealth.MissingDll,
            StatusEntryPointNotFound => EngineHealth.OsTooOld,
            StatusIllegalInstruction => EngineHealth.IllegalInstruction,
            _ => EngineHealth.Crashed,
        };

        string? versionLine = null;
        int? build = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("version:", StringComparison.OrdinalIgnoreCase))
            {
                versionLine = line.Trim();
                var m = VersionRx.Match(line);
                if (m.Success) build = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
            }
        }
        return new SmokeResult(health, code, versionLine, build, text);
    }

    /// <summary>Подавляет системные окна ошибок загрузки DLL у дочернего процесса (режим наследуется).</summary>
    private sealed class ErrorModeScope : IDisposable
    {
        private readonly uint _old;

        private ErrorModeScope(uint old) => _old = old;

        public static ErrorModeScope Silent() => new(SetErrorMode(0x0001 | 0x0002 | 0x8000)); // FAILCRITICALERRORS | NOGPFAULTERRORBOX | NOOPENFILEERRORBOX

        public void Dispose() => SetErrorMode(_old);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);
    }
}
