using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Net;
using OfflineChatter.Interop;
using OfflineChatter.Localization;

namespace OfflineChatter.Shell;

/// <summary>Подпись Authenticode исполняемого файла: цела ли она и кем подписан файл.</summary>
public static class Authenticode
{
    public static SignatureInfo Inspect(string path)
    {
        int result;
        var file = new WinTrust.FileInfo { cbStruct = Marshal.SizeOf<WinTrust.FileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(file.cbStruct);
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrust.Data
            {
                cbStruct = Marshal.SizeOf<WinTrust.Data>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x10,
            };
            var action = WinTrust.GenericVerifyV2;
            result = WinTrust.WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Update", "WinVerifyTrust is not available", ex);
            return new SignatureInfo(SignatureState.Unsigned);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrust.FileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }

        var state = result switch
        {
            0 => SignatureState.Valid,
            WinTrust.TRUST_E_NOSIGNATURE or WinTrust.TRUST_E_SUBJECT_FORM_UNKNOWN => SignatureState.Unsigned,
            WinTrust.CERT_E_UNTRUSTEDROOT or WinTrust.CERT_E_CHAINING => SignatureState.UntrustedRoot,
            _ => SignatureState.Invalid,
        };
        if (state == SignatureState.Unsigned) return new SignatureInfo(state);

        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var info = new SignatureInfo(state, cert.Subject, cert.Thumbprint);
            if (state == SignatureState.Invalid) Log.Warn("Update", $"Invalid signature 0x{result:X8}", null, new { path, cert.Subject });
            return info;
        }
        catch (CryptographicException)
        {
            return new SignatureInfo(state == SignatureState.Valid ? SignatureState.Invalid : state);
        }
    }

    /// <summary>Издатель для показа: CN из Subject.</summary>
    public static string? Publisher(SignatureInfo s)
    {
        if (string.IsNullOrEmpty(s.Subject)) return null;
        foreach (var part in s.Subject!.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p.Substring(3).Trim('"');
        }
        return s.Subject;
    }

    /// <summary>Можно ли заменить работающую программу этим файлом (правила — в UpdateSignaturePolicy).</summary>
    public static (UpdateSignatureVerdict verdict, string reason, SignatureInfo candidate) CheckUpdate(string currentExe, string newExe)
    {
        var current = Inspect(currentExe);
        var candidate = Inspect(newExe);
        var (verdict, reason) = UpdateSignaturePolicy.Check(current, candidate);
        Log.Info("Update", "Signature check", new
        {
            verdict = verdict.ToString(),
            reason,
            current = current.State.ToString(),
            candidate = candidate.State.ToString(),
            currentPublisher = Publisher(current),
            publisher = Publisher(candidate),
        });
        return (verdict, reason, candidate);
    }

    /// <summary>Текст вопроса «ставить ли обновление без действительной подписи».</summary>
    public static string ConsentText(string reason, SignatureInfo candidate, string tag) =>
        L.F("Update_SignatureAsk_" + reason, tag, Publisher(candidate) ?? "—");
}
