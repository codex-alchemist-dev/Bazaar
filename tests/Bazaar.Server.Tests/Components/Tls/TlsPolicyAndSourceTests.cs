using System.Net;
using Bazaar.Server.Components.Tls;

namespace Bazaar.Server.Tests.Components.Tls;

/// <summary>The plain-text loopback rule and the <c>tls.certificate_source</c> parser. Pure: no files, no network.</summary>
public sealed class TlsPolicyAndSourceTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.42.0.9")]
    [InlineData("::1")]
    public void Plain_text_is_allowed_on_loopback(string address) =>
        Assert.True(TlsPolicy.IsPlainTextAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("0.0.0.0")]
    [InlineData("fd00::20")]
    public void Plain_text_is_refused_off_loopback(string address) =>
        Assert.False(TlsPolicy.IsPlainTextAllowed(IPAddress.Parse(address)));

    [Fact]
    public void Plain_text_is_refused_for_a_missing_address() =>
        Assert.False(TlsPolicy.IsPlainTextAllowed((IPAddress?)null));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("", false)]
    public void Host_text_must_be_a_loopback_literal(string host, bool expected) =>
        Assert.Equal(expected, TlsPolicy.IsPlainTextAllowedForHost(host));

    [Fact]
    public void Source_default_is_pem_when_the_setting_is_unset()
    {
        Assert.Equal(TlsSourceKind.Pem, TlsSettings.ParseCertificateSource(null));
        Assert.Equal(TlsSourceKind.Pem, TlsSettings.ParseCertificateSource(""));
        Assert.Equal(TlsSourceKind.Pem, TlsSettings.ParseCertificateSource("   "));
        Assert.Equal(TlsSourceKind.Pem, TlsSettings.DefaultCertificateSource);
    }

    [Theory]
    [InlineData("pem", TlsSourceKind.Pem)]
    [InlineData("acme", TlsSourceKind.Acme)]
    [InlineData("overlay", TlsSourceKind.Overlay)]
    [InlineData(" ACME ", TlsSourceKind.Acme)]
    public void Source_parses_the_settings_names(string value, TlsSourceKind expected) =>
        Assert.Equal(expected, TlsSettings.ParseCertificateSource(value));

    [Fact]
    public void Source_rejects_an_unknown_name()
    {
        Assert.False(TlsSettings.TryParseCertificateSource("letsencrypt", out _));
        Assert.Throws<FormatException>(() => TlsSettings.ParseCertificateSource("letsencrypt"));
    }

    [Fact]
    public void Certificate_source_setting_name_is_the_documented_key() =>
        Assert.Equal("tls.certificate_source", TlsSettings.CertificateSourceName);
}
