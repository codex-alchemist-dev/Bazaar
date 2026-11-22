namespace Bazaar.Server.DataVolume.Windows;

/// <summary>A BitLocker WMI call failed. <see cref="ReturnValue"/> is the method's HRESULT-style return value, when it returned one.</summary>
internal sealed class BitLockerException(string message, uint? returnValue = null) : InvalidOperationException(message)
{
    public uint? ReturnValue { get; } = returnValue;
}

/// <summary>The Win32_EncryptableVolume <c>ConversionStatus</c> values the volume unit cares about.</summary>
internal static class BitLockerConversion
{
    public const int FullyDecrypted = 0;
    public const int FullyEncrypted = 1;
    public const int EncryptionInProgress = 2;
}

internal static class BitLockerValues
{
    /// <summary>EncryptionMethod 7: XTS-AES-256.</summary>
    public const int XtsAes256 = 7;

    /// <summary>EncryptionFlags 1: encrypt used space only.</summary>
    public const int UsedSpaceOnly = 1;

    public const int ProtectionOn = 1;
    public const int Locked = 1;
    public const int Unlocked = 0;
}

/// <summary>
/// The Win32_EncryptableVolume calls of doc 63 section 3.1 (spike D1), addressed by canonical volume GUID. Every call needs
/// an administrator token. Methods throw <see cref="BitLockerException"/> when the call fails, except
/// <see cref="LockAsync"/>, which returns the raw return value because "files are open" is an expected answer.
/// </summary>
internal interface IBitLockerVolumes
{
    /// <summary>Adds an external-key protector holding <paramref name="externalKey"/> (32 bytes). The key is never written to a file.</summary>
    ValueTask ProtectKeyWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken);

    ValueTask EncryptAsync(string volumeGuid, int encryptionMethod, int encryptionFlags, CancellationToken cancellationToken);

    /// <summary>See <see cref="BitLockerConversion"/>.</summary>
    ValueTask<int> GetConversionStatusAsync(string volumeGuid, CancellationToken cancellationToken);

    /// <summary>1 when protection is on.</summary>
    ValueTask<int> GetProtectionStatusAsync(string volumeGuid, CancellationToken cancellationToken);

    /// <summary>The encryption method number; 7 is XTS-AES-256.</summary>
    ValueTask<int> GetEncryptionMethodAsync(string volumeGuid, CancellationToken cancellationToken);

    /// <summary>0 unlocked, 1 locked.</summary>
    ValueTask<int> GetLockStatusAsync(string volumeGuid, CancellationToken cancellationToken);

    ValueTask UnlockWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken);

    /// <summary>Locks the volume. Returns the WMI return value, 0 on success. Only the volume unit's close path calls it, with <paramref name="forceDismount"/> false.</summary>
    ValueTask<uint> LockAsync(string volumeGuid, bool forceDismount, CancellationToken cancellationToken);
}
