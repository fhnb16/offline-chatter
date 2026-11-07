using System;

namespace OfflineChatter.Core.Net;

/// <summary>Состояние подписи Authenticode файла.</summary>
public enum SignatureState
{
    /// <summary>Подписи нет.</summary>
    Unsigned,

    /// <summary>Подпись цела, цепочка доверена Windows (сертификат удостоверяющего центра).</summary>
    Valid,

    /// <summary>Подпись цела, но корень не доверен (самоподписанный сертификат).</summary>
    UntrustedRoot,

    /// <summary>Подпись повреждена или файл изменён после подписи.</summary>
    Invalid,
}

/// <summary>Подпись файла: состояние и сертификат подписавшего.</summary>
public sealed record SignatureInfo(SignatureState State, string? Subject = null, string? Thumbprint = null)
{
    public bool IsSigned => State is SignatureState.Valid or SignatureState.UntrustedRoot;
}

/// <summary>Решение по подписи скачанного обновления.</summary>
public enum UpdateSignatureVerdict
{
    /// <summary>Ставить без вопросов.</summary>
    Allow,

    /// <summary>Подписи нет или ей не доверяет Windows, хотя текущая версия подписана: ставить только с согласия пользователя.</summary>
    Ask,

    /// <summary>Не ставить: подпись повреждена.</summary>
    Reject,
}

/// <summary>
/// Можно ли ставить скачанное обновление с точки зрения подписи. Дополняет проверку SHA-256 из официального API.
/// Правила:
///  • повреждённая подпись (файл изменён после подписания) — нельзя никогда;
///  • действительная подпись (цепочка доверена Windows) — ставим, даже если сертификат или издатель сменился:
///    сертификат продлевают, меняют удостоверяющий центр;
///  • текущая не подписана — ставим любой целый файл;
///  • тот же самоподписанный сертификат — ставим;
///  • иначе (подписи нет, недоверенный или другой самоподписанный сертификат) — спрашиваем пользователя:
///    сертификат могут отозвать или не продлить, обновления при этом не должны останавливаться.
/// </summary>
public static class UpdateSignaturePolicy
{
    public static (UpdateSignatureVerdict verdict, string reason) Check(SignatureInfo current, SignatureInfo candidate)
    {
        if (candidate.State == SignatureState.Invalid) return (UpdateSignatureVerdict.Reject, "SignatureInvalid");
        if (candidate.State == SignatureState.Valid)
            return (UpdateSignatureVerdict.Allow, !current.IsSigned || SamePublisher(current, candidate) ? "SamePublisher" : "PublisherChanged");
        if (!current.IsSigned) return (UpdateSignatureVerdict.Allow, "CurrentUnsigned");
        if (candidate.State == SignatureState.Unsigned) return (UpdateSignatureVerdict.Ask, "SignatureMissing");

        // Кандидат подписан самоподписанным сертификатом
        if (current.State == SignatureState.UntrustedRoot &&
            string.Equals(current.Thumbprint, candidate.Thumbprint, StringComparison.OrdinalIgnoreCase))
            return (UpdateSignatureVerdict.Allow, "SameCertificate");
        return (UpdateSignatureVerdict.Ask, current.State == SignatureState.Valid ? "SignatureUntrusted" : "CertificateChanged");
    }

    private static bool SamePublisher(SignatureInfo a, SignatureInfo b) =>
        string.Equals(Normalize(a.Subject), Normalize(b.Subject), StringComparison.OrdinalIgnoreCase);

    // «CN=Имя, O=Организация, C=RU» — порядок и пробелы могут отличаться у разных сертификатов одного издателя
    private static string Normalize(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
        var parts = subject!.Split(',');
        for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        Array.Sort(parts, StringComparer.OrdinalIgnoreCase);
        return string.Join(",", parts);
    }
}
