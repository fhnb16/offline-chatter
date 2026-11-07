using OfflineChatter.Core.Net;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class UpdateSignatureTests
{
    private static SignatureInfo Ca(string subject, string thumb = "AA") => new(SignatureState.Valid, subject, thumb);
    private static SignatureInfo Self(string thumb, string subject = "CN=Offline Chatter (test)") => new(SignatureState.UntrustedRoot, subject, thumb);
    private static readonly SignatureInfo Unsigned = new(SignatureState.Unsigned);
    private static readonly SignatureInfo Broken = new(SignatureState.Invalid, "CN=Offline Chatter", "AA");

    private static UpdateSignatureVerdict V(SignatureInfo current, SignatureInfo candidate) => UpdateSignaturePolicy.Check(current, candidate).verdict;

    [Fact]
    public void Broken_Signature_Is_Never_Allowed()
    {
        Assert.Equal(UpdateSignatureVerdict.Reject, V(Unsigned, Broken));
        Assert.Equal(UpdateSignatureVerdict.Reject, V(Ca("CN=Offline Chatter"), Broken));
        Assert.Equal(UpdateSignatureVerdict.Reject, V(Self("T1"), Broken));
    }

    [Fact]
    public void Unsigned_Current_Accepts_Anything_Intact()
    {
        Assert.Equal(UpdateSignatureVerdict.Allow, V(Unsigned, Unsigned));
        Assert.Equal(UpdateSignatureVerdict.Allow, V(Unsigned, Ca("CN=X")));
        Assert.Equal(UpdateSignatureVerdict.Allow, V(Unsigned, Self("T1")));
    }

    [Fact]
    public void Valid_Signature_Is_Accepted_Even_If_Certificate_Or_Publisher_Changed()
    {
        var current = Ca("CN=Offline Chatter, O=fhnb16, C=RU", "OLD");
        var (verdict, reason) = UpdateSignaturePolicy.Check(current, Ca("C=RU, O=fhnb16, CN=Offline Chatter", "NEW"));
        Assert.Equal(UpdateSignatureVerdict.Allow, verdict);
        Assert.Equal("SamePublisher", reason);

        (verdict, reason) = UpdateSignaturePolicy.Check(current, Ca("CN=Someone Else", "NEW"));
        Assert.Equal(UpdateSignatureVerdict.Allow, verdict);
        Assert.Equal("PublisherChanged", reason);

        Assert.Equal(UpdateSignatureVerdict.Allow, V(Self("T1"), Ca("CN=Offline Chatter")));
    }

    [Fact]
    public void Missing_Or_Untrusted_Signature_Needs_Consent()
    {
        var ca = Ca("CN=Offline Chatter");
        Assert.Equal((UpdateSignatureVerdict.Ask, "SignatureMissing"), UpdateSignaturePolicy.Check(ca, Unsigned));
        Assert.Equal((UpdateSignatureVerdict.Ask, "SignatureUntrusted"), UpdateSignaturePolicy.Check(ca, Self("T1")));
        Assert.Equal((UpdateSignatureVerdict.Ask, "SignatureMissing"), UpdateSignaturePolicy.Check(Self("T1"), Unsigned));
    }

    [Fact]
    public void Self_Signed_Same_Certificate_Is_Allowed_Other_Needs_Consent()
    {
        Assert.Equal(UpdateSignatureVerdict.Allow, V(Self("T1"), Self("t1")));
        // Тот же Subject, другой ключ: возможна подделка — решает пользователь
        Assert.Equal((UpdateSignatureVerdict.Ask, "CertificateChanged"), UpdateSignaturePolicy.Check(Self("T1"), Self("T2")));
    }
}
