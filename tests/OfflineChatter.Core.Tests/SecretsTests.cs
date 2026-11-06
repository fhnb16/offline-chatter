using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

[Collection("Secrets")] // статическое состояние — тесты последовательно
public class SecretsTests
{
    [Fact]
    public void Dpapi_And_Master_Password_Roundtrip()
    {
        var settings = new SecuritySettings();
        Secrets.LoadMode(settings);

        var dpapi = Secrets.Protect("sk-test-123456");
        Assert.StartsWith("dpapi:", dpapi);
        Assert.Equal("sk-test-123456", Secrets.UnprotectOrEmpty(dpapi));

        Secrets.EnableMasterPassword("correct horse");
        var mp = Secrets.Protect("hf_token_value");
        Assert.StartsWith("mp:", mp);
        Assert.Equal("hf_token_value", Secrets.UnprotectOrEmpty(mp));
        Assert.Equal("sk-test-123456", Secrets.UnprotectOrEmpty(dpapi)); // старые значения читаются до перешифровки
        Assert.StartsWith("mp:", Secrets.Reprotect(dpapi));

        // «Новый запуск»: ключ не в памяти, нужен пароль
        var restarted = new SecuritySettings { Mode = settings.Mode, Salt = settings.Salt, Verifier = settings.Verifier };
        Secrets.LoadMode(restarted);
        typeof(Secrets).GetField("_master", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
        Assert.True(Secrets.NeedsUnlock);
        Assert.False(Secrets.TryUnprotect(mp, out _));
        Assert.False(Secrets.Unlock("wrong"));
        Assert.True(Secrets.Unlock("correct horse"));
        Assert.Equal("hf_token_value", Secrets.UnprotectOrEmpty(mp));

        // Подмена шифротекста обнаруживается (HMAC)
        var tampered = mp.Substring(0, mp.Length - 6) + (mp[mp.Length - 6] == 'A' ? 'B' : 'A') + mp.Substring(mp.Length - 5);
        Assert.False(Secrets.TryUnprotect(tampered, out _));

        Secrets.DisableMasterPassword();
        Assert.StartsWith("dpapi:", Secrets.Reprotect(mp));
        Secrets.LoadMode(new SecuritySettings());
    }

    [Fact]
    public void Mask() => Assert.Equal("sk-…cdef", Secrets.Mask("sk-1234567890abcdef"));
}
