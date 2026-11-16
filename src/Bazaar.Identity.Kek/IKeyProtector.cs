namespace Bazaar.Identity.Kek;

/// <summary>
/// Protects the KEK bytes at rest with an operating-system facility. The seam keeps <see cref="DpapiKekProvider"/> testable
/// off Windows; the real implementation is <see cref="DpapiKeyProtector"/>.
/// </summary>
public interface IKeyProtector
{
    /// <summary>Returns the protected form of <paramref name="plaintext"/>.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>Returns the plaintext of <paramref name="protectedBytes"/>. Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the protection does not match this host.</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}
