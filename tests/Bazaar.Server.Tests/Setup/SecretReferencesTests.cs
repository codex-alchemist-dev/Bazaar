using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>Reference syntax (doc 45 section 1.6). Only the shape is checked; nothing is resolved.</summary>
public sealed class SecretReferencesTests
{
    [Theory]
    [InlineData("env:BAZAAR_KEK")]
    [InlineData("file:/run/secrets/kek")]
    [InlineData("dpapi:C:\\ProgramData\\Bazaar\\kek.bin")]
    [InlineData("systemd-creds:bazaar-kek")]
    public void Bootstrap_references_are_accepted_for_bootstrap_values(string reference)
    {
        Assert.True(SecretReferences.IsValid(reference, SecretReferences.BootstrapFamilies));
    }

    [Theory]
    [InlineData("secret://bazaar/owner/app-id")]
    [InlineData("secret://bazaar/providers/anthropic/key")]
    public void Store_references_are_accepted_for_store_values(string reference)
    {
        Assert.True(SecretReferences.IsValid(reference, SecretReferences.SecretStoreFamilies));
    }

    [Fact]
    public void A_store_reference_is_refused_for_a_bootstrap_value()
    {
        Assert.False(SecretReferences.IsValid("secret://bazaar/kek", SecretReferences.BootstrapFamilies));
    }

    [Fact]
    public void A_bootstrap_reference_is_refused_for_a_store_value()
    {
        Assert.False(SecretReferences.IsValid("env:BAZAAR_KEK", SecretReferences.SecretStoreFamilies));
    }

    [Theory]
    [InlineData(SetupFixtures.Canary)]
    [InlineData("env:")]
    [InlineData("env:1BAD")]
    [InlineData("env:HAS SPACE")]
    [InlineData("file:")]
    [InlineData("file:bad\nline")]
    [InlineData("dpapi:")]
    [InlineData("systemd-creds:bad name")]
    [InlineData("secret://bazaar/")]
    [InlineData("secret://bazaar//double")]
    [InlineData("secret://other/owner/key")]
    [InlineData("https://example.invalid/key")]
    [InlineData("")]
    public void Literal_values_and_malformed_references_are_refused(string text)
    {
        Assert.False(SecretReferences.IsValid(text, SecretReferences.BootstrapFamilies));
        Assert.False(SecretReferences.IsValid(text, SecretReferences.SecretStoreFamilies));
    }

    [Fact]
    public void Classify_names_the_family_and_none_for_plain_text()
    {
        Assert.Equal(SecretReferenceFamily.Dpapi, SecretReferences.Classify("dpapi:C:\\x"));
        Assert.Equal(SecretReferenceFamily.SecretStore, SecretReferences.Classify("secret://bazaar/a"));
        Assert.Equal(SecretReferenceFamily.None, SecretReferences.Classify(SetupFixtures.Canary));
    }
}
