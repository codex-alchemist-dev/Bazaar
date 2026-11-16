namespace Bazaar.CompanyIdentity;

/// <summary>
/// The identity a company build carries (doc 37 section 4a): the server's address and the SHA-256 pin of its key. Built only by
/// <see cref="Create"/>, which refuses anything a client would refuse at connect time.
/// </summary>
public sealed record CompanyServerIdentity
{
    private CompanyServerIdentity(Uri address, string keyHashPin)
    {
        Address = address;
        KeyHashPin = keyHashPin;
    }

    /// <summary>The address clients connect to. https, or plain http on loopback only.</summary>
    public Uri Address { get; }

    /// <summary>Lowercase hex SHA-256 of the server key's SubjectPublicKeyInfo (64 characters).</summary>
    public string KeyHashPin { get; }

    /// <summary>Validates the address and pin, then builds the identity.</summary>
    /// <exception cref="ArgumentException">The address or pin is not acceptable.</exception>
    public static CompanyServerIdentity Create(Uri address, string keyHashPin)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(keyHashPin);

        if (!address.IsAbsoluteUri)
            throw new ArgumentException("The server address must be absolute.", nameof(address));
        if (!string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("The server address must not carry user information.", nameof(address));
        if (address.Scheme == Uri.UriSchemeHttp && !IsLoopback(address))
            throw new ArgumentException("Plain http is allowed only on loopback. Use https for a company server.", nameof(address));
        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("The server address must use https.", nameof(address));

        if (!IsWellFormedPin(keyHashPin))
            throw new ArgumentException("The key-hash pin must be 64 lowercase hex characters.", nameof(keyHashPin));

        return new CompanyServerIdentity(address, keyHashPin);
    }

    private static bool IsLoopback(Uri address) =>
        address.IsLoopback || string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsWellFormedPin(string pin)
    {
        if (pin.Length != 64) return false;
        foreach (var c in pin)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }
}
