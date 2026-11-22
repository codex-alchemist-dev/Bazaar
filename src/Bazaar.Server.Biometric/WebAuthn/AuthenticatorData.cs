using System.Buffers.Binary;

namespace Bazaar.Server.Biometric.WebAuthn;

/// <summary>The fixed 37-byte head of WebAuthn authenticator data: rpIdHash, flags and the sign count. Any extension bytes after it are ignored.</summary>
internal readonly record struct AuthenticatorData(byte[] RpIdHash, byte Flags, uint SignCount)
{
    public const int MinLength = 37;
    public const int MaxLength = 1024;

    private const byte _userPresent = 0x01;
    private const byte _userVerified = 0x04;
    private const byte _backupEligible = 0x08;
    private const byte _backupState = 0x10;

    public bool UserPresent => (Flags & _userPresent) != 0;

    public bool UserVerified => (Flags & _userVerified) != 0;

    /// <summary>True for a synced (multi-device) credential: either backup flag is set.</summary>
    public bool Synced => (Flags & (_backupEligible | _backupState)) != 0;

    public static bool TryParse(ReadOnlySpan<byte> data, out AuthenticatorData parsed)
    {
        parsed = default;
        if (data.Length is < MinLength or > MaxLength)
            return false;

        parsed = new AuthenticatorData(data[..32].ToArray(), data[32], BinaryPrimitives.ReadUInt32BigEndian(data.Slice(33, 4)));
        return true;
    }
}
