using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// A server identity read once: the address clients connect to and the pin of the server key. The Host adapter builds it from the
/// loaded TLS certificate's public-key hash, so this module never touches a key. The pin is a public hash, never a secret.
/// </summary>
public sealed class StaticServerIdentitySource : IServerIdentitySource
{
    /// <summary>Creates the source. The pin must be 64 lowercase hex characters, as <c>PublicKeyPin.Compute</c> returns it.</summary>
    /// <exception cref="ArgumentException">The address is not absolute or the pin is not 64 lowercase hex characters.</exception>
    public StaticServerIdentitySource(Uri address, string keyHashPin)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(keyHashPin);
        if (!address.IsAbsoluteUri)
            throw new ArgumentException("The server address must be absolute.", nameof(address));
        if (keyHashPin.Length != 64 || !keyHashPin.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new ArgumentException("The key pin must be 64 lowercase hex characters.", nameof(keyHashPin));
        Address = address;
        KeyHashPin = keyHashPin;
    }

    /// <inheritdoc />
    public Uri Address { get; }

    /// <inheritdoc />
    public string KeyHashPin { get; }
}
