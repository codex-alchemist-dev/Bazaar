namespace Bazaar.Server.Identity.Kek.OpenBao;

/// <summary>
/// Operator settings for the OpenBao transit KEK (doc 14, <c>identity.kek_provider = openbao</c>). Every value is host configuration
/// because the KEK must open before the database does. The token is a bootstrap reference (<c>env:NAME</c> or <c>file:&lt;path&gt;</c>),
/// never a literal, and is read on each request so an operator can rotate it without a restart.
/// </summary>
public sealed record OpenBaoOptions(Uri Address, string Mount, string KeyName, string TokenRef, string? Namespace, TimeSpan Timeout)
{
    public const string AddressKey = "Bazaar:Identity:OpenBao:Address";
    public const string MountKey = "Bazaar:Identity:OpenBao:Mount";
    public const string KeyNameKey = "Bazaar:Identity:OpenBao:KeyName";
    public const string TokenRefKey = "Bazaar:Identity:OpenBao:TokenRef";
    public const string NamespaceKey = "Bazaar:Identity:OpenBao:Namespace";
    public const string TimeoutSecondsKey = "Bazaar:Identity:OpenBao:TimeoutSeconds";

    public const string DefaultMount = "transit";
    public const string DefaultKeyName = "bazaar-kek";
    public const int DefaultTimeoutSeconds = 10;

    /// <summary>Reads and validates the settings. A bad value throws <see cref="ArgumentException"/>; nothing falls back to another provider.</summary>
    public static OpenBaoOptions From(Func<string, string?> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        var address = Required(setting, AddressKey);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException($"{AddressKey} must be an absolute http(s) URL.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new ArgumentException($"{AddressKey} must use https unless OpenBao runs on this host (loopback).");

        var mount = Segment(setting(MountKey), DefaultMount, MountKey);
        var keyName = Segment(setting(KeyNameKey), DefaultKeyName, KeyNameKey);
        var tokenRef = Required(setting, TokenRefKey);
        if (!tokenRef.StartsWith("env:", StringComparison.Ordinal) && !tokenRef.StartsWith("file:", StringComparison.Ordinal))
            throw new ArgumentException($"{TokenRefKey} must be a bootstrap reference: 'env:NAME' or 'file:<path>'.");

        var timeoutSeconds = DefaultTimeoutSeconds;
        var rawTimeout = setting(TimeoutSecondsKey);
        if (!string.IsNullOrWhiteSpace(rawTimeout) && (!int.TryParse(rawTimeout, out timeoutSeconds) || timeoutSeconds is < 1 or > 300))
            throw new ArgumentException($"{TimeoutSecondsKey} must be a whole number from 1 to 300.");

        var ns = setting(NamespaceKey);
        return new OpenBaoOptions(uri, mount, keyName, tokenRef, string.IsNullOrWhiteSpace(ns) ? null : ns.Trim(), TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static string Required(Func<string, string?> setting, string key) =>
        setting(key) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{key} is required when identity.kek_provider is 'openbao'.");

    /// <summary>A mount or key name goes into the URL path, so it is limited to letters, digits, dot, dash, underscore and slash (mount only).</summary>
    private static string Segment(string? value, string fallback, string key)
    {
        var chosen = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Trim('/');
        if (chosen.Length == 0 || chosen.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/')) || chosen.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"{key} may hold only letters, digits, '.', '-', '_' and '/'.");
        if (key == KeyNameKey && chosen.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException($"{key} may not contain '/'.");
        return chosen;
    }
}
