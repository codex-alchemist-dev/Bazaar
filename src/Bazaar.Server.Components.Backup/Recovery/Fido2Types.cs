namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>A registered backup-owner FIDO2 credential. <see cref="CoseKey"/> is the COSE_Key bytes stored at registration.</summary>
public sealed record Fido2Credential(byte[] CredentialId, byte[] CoseKey, uint SignCount);

/// <summary>Outcome of verifying one assertion. On acceptance the caller must persist <see cref="NewSignCount"/> for <see cref="CredentialId"/>.</summary>
public sealed record Fido2AssertionResult
{
    private Fido2AssertionResult()
    {
    }

    /// <summary>True when every check passed.</summary>
    public bool Accepted { get; private init; }

    /// <summary>A short fixed reason code; never contains key material.</summary>
    public string Reason { get; private init; } = "";

    /// <summary>The credential that signed (accepted only).</summary>
    public byte[]? CredentialId { get; private init; }

    /// <summary>The counter to store (accepted only).</summary>
    public uint NewSignCount { get; private init; }

    internal static Fido2AssertionResult Accept(byte[] credentialId, uint signCount) =>
        new() { Accepted = true, Reason = "accepted", CredentialId = credentialId, NewSignCount = signCount };

    internal static Fido2AssertionResult Refuse(string reason) => new() { Accepted = false, Reason = reason };
}
