namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>Why a recovery bundle could not be unsealed.</summary>
public enum RecoveryBundleFailure
{
    /// <summary>The file is shorter than the fixed header.</summary>
    Truncated,

    /// <summary>The first four bytes are not the bundle magic.</summary>
    BadMagic,

    /// <summary>The format version (header byte or payload version) is not one this build reads.</summary>
    UnknownVersion,

    /// <summary>The KDF id byte names no KDF this build knows.</summary>
    UnknownKdf,

    /// <summary>A header field is out of range, such as an iteration count below the floor or above the ceiling.</summary>
    InvalidHeader,

    /// <summary>
    /// Authentication failed. AES-GCM cannot tell a wrong passphrase from an altered file, so both report this value.
    /// Truncation inside the ciphertext also lands here, because the format has no length field.
    /// </summary>
    Tampered,

    /// <summary>The authenticated plaintext is not a valid payload.</summary>
    InvalidPayload,
}

/// <summary>Thrown when a recovery bundle cannot be unsealed. Messages never include the passphrase or key bytes.</summary>
public sealed class RecoveryBundleException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RecoveryBundleException(RecoveryBundleFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>The failure kind, for callers and tests.</summary>
    public RecoveryBundleFailure Failure { get; }
}
