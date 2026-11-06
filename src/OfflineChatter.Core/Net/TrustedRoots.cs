using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Net;

/// <summary>
/// Уровень 2: проверка цепочки по вшитому набору корневых сертификатов, без установки чего-либо в систему.
/// Нужен на Win7 без обновлений, где в хранилище нет ISRG Root X1, Amazon Root CA, GTS и т.п.
/// Источники: ресурсы сборки (Net/Roots/*.cer) + пользовательские файлы config/certs/*.cer|*.crt|*.pem.
/// </summary>
public sealed class TrustedRoots
{
    private readonly X509Certificate2Collection _roots = new();
    private readonly HashSet<string> _thumbprints = new(StringComparer.OrdinalIgnoreCase);

    public TrustedRoots(string? userCertsDirectory = null)
    {
        var asm = typeof(TrustedRoots).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Roots.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            Add(new X509Certificate2(ms.ToArray()));
        }

        if (userCertsDirectory != null && Directory.Exists(userCertsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(userCertsDirectory))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".cer" or ".crt" or ".pem")) continue;
                try
                {
                    foreach (var c in LoadFile(file)) Add(c);
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or FormatException)
                {
                    Log.Warn("Net", "Cannot load user certificate " + file, ex);
                }
            }
        }
    }

    public int Count => _roots.Count;

    public IEnumerable<X509Certificate2> Certificates => _roots.Cast<X509Certificate2>();

    private void Add(X509Certificate2 cert)
    {
        if (_thumbprints.Add(cert.Thumbprint)) _roots.Add(cert);
    }

    private static IEnumerable<X509Certificate2> LoadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.Contains("-----BEGIN CERTIFICATE-----"))
        {
            yield return new X509Certificate2(bytes);
            yield break;
        }

        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        var pos = 0;
        while ((pos = text.IndexOf(begin, pos, StringComparison.Ordinal)) >= 0)
        {
            var stop = text.IndexOf(end, pos, StringComparison.Ordinal);
            if (stop < 0) yield break;
            var b64 = text.Substring(pos + begin.Length, stop - pos - begin.Length);
            yield return new X509Certificate2(Convert.FromBase64String(b64.Replace("\r", "").Replace("\n", "").Trim()));
            pos = stop + end.Length;
        }
    }

    /// <summary>
    /// Колбэк для HttpClientHandler.ServerCertificateCustomValidationCallback.
    /// Принимает то, что приняла ОС; иначе перестраивает цепочку с нашими корнями.
    /// Несоответствие имени хоста не прощается никогда.
    /// </summary>
    public bool Validate(X509Certificate2? leaf, X509Chain? systemChain, SslPolicyErrors errors, string host)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (leaf == null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            Log.Warn("Net", $"TLS validation failed for {host}: {errors}");
            return false;
        }

        var intermediates = systemChain?.ChainElements.Cast<X509ChainElement>().Skip(1).Select(e => e.Certificate);
        var ok = ValidateChain(leaf, intermediates, out var reason);
        if (!ok) Log.Warn("Net", $"Pinned-roots validation failed for {host}: {reason}");
        return ok;
    }

    /// <summary>Строит цепочку с нашими корнями в ExtraStore и требует, чтобы она заканчивалась одним из них.</summary>
    public bool ValidateChain(X509Certificate2 leaf, IEnumerable<X509Certificate2>? intermediates, out string reason)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(_roots);
        if (intermediates != null)
            foreach (var c in intermediates) chain.ChainPolicy.ExtraStore.Add(c);

        // На Win7 без сети проверка отзыва может висеть десятки секунд — в резервном режиме отключаем её.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        chain.Build(leaf);

        var elements = chain.ChainElements;
        if (elements.Count == 0)
        {
            reason = "empty chain";
            return false;
        }

        var root = elements[elements.Count - 1].Certificate;
        if (!_thumbprints.Contains(root.Thumbprint))
        {
            reason = "chain ends at unknown root: " + root.Subject;
            return false;
        }

        foreach (X509ChainElement e in elements)
        {
            foreach (var st in e.ChainElementStatus)
            {
                if (st.Status is X509ChainStatusFlags.NoError or X509ChainStatusFlags.UntrustedRoot
                    or X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation)
                    continue;
                reason = $"{e.Certificate.Subject}: {st.Status} {st.StatusInformation}".Trim();
                return false;
            }
        }

        reason = "ok";
        return true;
    }
}
