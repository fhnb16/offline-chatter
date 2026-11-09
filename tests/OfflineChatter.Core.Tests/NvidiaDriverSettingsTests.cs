using OfflineChatter.Core.Diagnostics;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class NvidiaDriverSettingsTests
{
    [Theory]
    [InlineData("616.64", true)]
    [InlineData("536.40", true)]
    [InlineData("536.23", false)]
    [InlineData("474.44", false)] // последняя ветка для Win7
    [InlineData("537", true)]
    public void Policy_Exists_Since_536_40(string version, bool expected) =>
        Assert.Equal(expected, NvidiaDriverSettings.DriverHasPolicy(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void Unknown_Driver_Version(string? version) => Assert.Null(NvidiaDriverSettings.DriverHasPolicy(version));

    [Fact]
    public void Values_Map_To_Policy()
    {
        Assert.Equal(SysmemFallbackPolicy.DriverDefault, NvidiaDriverSettings.PolicyFromValue(0));
        Assert.Equal(SysmemFallbackPolicy.PreferNoFallback, NvidiaDriverSettings.PolicyFromValue(1));
        Assert.Equal(SysmemFallbackPolicy.PreferFallback, NvidiaDriverSettings.PolicyFromValue(2));
        Assert.Equal(SysmemFallbackPolicy.Unknown, NvidiaDriverSettings.PolicyFromValue(7));
        Assert.True(new SysmemFallbackInfo(SysmemFallbackPolicy.DriverDefault, false, null, null).FallbackAllowed);
        Assert.False(new SysmemFallbackInfo(SysmemFallbackPolicy.PreferNoFallback, true, 1, null).FallbackAllowed);
        Assert.False(new SysmemFallbackInfo(SysmemFallbackPolicy.NotSupported, false, null, null).FallbackAllowed);
    }

    [Fact]
    public void Old_Driver_Is_Not_Supported_Without_Touching_Nvapi()
    {
        var info = NvidiaDriverSettings.ReadSysmemFallback("llama-server.exe", "474.44");
        Assert.Equal(SysmemFallbackPolicy.NotSupported, info.Policy);
    }
}
