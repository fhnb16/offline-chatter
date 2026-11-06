using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Storage;

public enum SecretsMode
{
    /// <summary>DPAPI текущего пользователя Windows: удобно, но не переносится на другой ПК.</summary>
    Dpapi,
    /// <summary>Мастер-пароль: работает на любом ПК (портативный режим), пароль спрашивается при запуске.</summary>
    MasterPassword,
}

public sealed class SecuritySettings
{
    public SecretsMode Mode { get; set; } = SecretsMode.Dpapi;
    /// <summary>Соль PBKDF2 (base64).</summary>
    public string? Salt { get; set; }
    /// <summary>Зашифрованная проверочная строка — чтобы понять, что пароль введён верно.</summary>
    public string? Verifier { get; set; }
}

/// <summary>
/// Хранение секретов (API-ключи, токен HF, пароль прокси).
/// "dpapi:base64" — DPAPI (CurrentUser) с энтропией приложения;
/// "mp:base64" — мастер-пароль: PBKDF2-SHA256 (200 000 итераций) → AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC).
/// Если расшифровать не удалось (другой ПК, неверный пароль) — <see cref="TryUnprotect"/> возвращает false, и UI просит ввести ключ заново.
/// </summary>
public static class Secrets
{
    private const string DpapiPrefix = "dpapi:";
    private const string MasterPrefix = "mp:";
    private const string VerifierText = "offline-chatter-verifier";
    private const int Iterations = 200_000;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OfflineChatter.v1.secrets");

    private static SecuritySettings _settings = new();
    private static byte[]? _master;

    public static void LoadMode(SecuritySettings settings) => _settings = settings;

    public static SecretsMode Mode => _settings.Mode;

    /// <summary>Включён мастер-пароль, но он ещё не введён в этом сеансе.</summary>
    public static bool NeedsUnlock => _settings.Mode == SecretsMode.MasterPassword && _master == null;

    public static bool Unlock(string password)
    {
        if (_settings.Salt == null || _settings.Verifier == null) return false;
        var key = Derive(password, Convert.FromBase64String(_settings.Salt));
        if (!TryDecryptMaster(_settings.Verifier, key, out var check) || check != VerifierText) return false;
        _master = key;
        return true;
    }

    /// <summary>Включить мастер-пароль. Существующие секреты нужно перешифровать через <see cref="Reprotect"/>.</summary>
    public static void EnableMasterPassword(string password)
    {
        var salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        var key = Derive(password, salt);
        _settings.Salt = Convert.ToBase64String(salt);
        _settings.Verifier = EncryptMaster(VerifierText, key);
        _settings.Mode = SecretsMode.MasterPassword;
        _master = key;
    }

    public static void DisableMasterPassword()
    {
        _settings.Mode = SecretsMode.Dpapi;
        _settings.Salt = null;
        _settings.Verifier = null;
        // ключ оставляем в памяти до конца сеанса, чтобы перешифровать старые значения
    }

    /// <summary>Перешифровать значение в текущем режиме (после смены режима или пароля).</summary>
    public static string Reprotect(string? stored) => TryUnprotect(stored, out var plain) && plain.Length > 0 ? Protect(plain) : stored ?? string.Empty;

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        if (_settings.Mode == SecretsMode.MasterPassword && _master != null) return EncryptMaster(plain!, _master);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(data);
    }

    public static bool TryUnprotect(string? stored, out string plain)
    {
        plain = string.Empty;
        if (string.IsNullOrEmpty(stored)) return true;
        if (stored!.StartsWith(MasterPrefix, StringComparison.Ordinal))
            return _master != null && TryDecryptMaster(stored, _master, out plain);
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(DpapiPrefix.Length)), Entropy, DataProtectionScope.CurrentUser);
            plain = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("Secrets", "Cannot decrypt a stored secret (moved to another PC/user?)");
            return false;
        }
    }

    public static string UnprotectOrEmpty(string? stored) => TryUnprotect(stored, out var p) ? p : string.Empty;

    /// <summary>Маскирует секрет для логов и UI: "sk-…a1b2".</summary>
    public static string Mask(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        return plain!.Length <= 8 ? new string('•', plain.Length) : plain.Substring(0, 3) + "…" + plain.Substring(plain.Length - 4);
    }

    // ---------------- мастер-пароль ----------------

    private static byte[] Derive(string password, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }

    private static (byte[] enc, byte[] mac) SubKeys(byte[] master)
    {
        using var h = new HMACSHA256(master);
        return (h.ComputeHash(Encoding.ASCII.GetBytes("enc")), h.ComputeHash(Encoding.ASCII.GetBytes("mac")));
    }

    private static string EncryptMaster(string plain, byte[] master)
    {
        var (encKey, macKey) = SubKeys(master);
        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        byte[] cipher;
        using (var enc = aes.CreateEncryptor())
        {
            var data = Encoding.UTF8.GetBytes(plain);
            cipher = enc.TransformFinalBlock(data, 0, data.Length);
        }
        var payload = aes.IV.Concat(cipher).ToArray();
        using var hmac = new HMACSHA256(macKey);
        var tag = hmac.ComputeHash(payload);
        return MasterPrefix + Convert.ToBase64String(payload.Concat(tag).ToArray());
    }

    private static bool TryDecryptMaster(string stored, byte[] master, out string plain)
    {
        plain = string.Empty;
        try
        {
            var all = Convert.FromBase64String(stored.Substring(MasterPrefix.Length));
            if (all.Length < 16 + 16 + 32) return false;
            var payload = all.Take(all.Length - 32).ToArray();
            var tag = all.Skip(all.Length - 32).ToArray();
            var (encKey, macKey) = SubKeys(master);
            using (var hmac = new HMACSHA256(macKey))
            {
                var expected = hmac.ComputeHash(payload);
                var diff = 0;
                for (var i = 0; i < 32; i++) diff |= expected[i] ^ tag[i]; // сравнение за постоянное время
                if (diff != 0) return false;
            }
            using var aes = Aes.Create();
            aes.Key = encKey;
            aes.IV = payload.Take(16).ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            plain = Encoding.UTF8.GetString(dec.TransformFinalBlock(payload, 16, payload.Length - 16));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
