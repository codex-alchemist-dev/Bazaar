using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.SignIn.WebAuthn;

/// <summary>
/// Operator settings for passkey verification and registration. Nothing has a default that points at a host: the relying-party id
/// (<c>signin:rpId</c>, shared with the sign-in page) and the exact origins (<c>signin:webauthn:origins</c>, a list or a
/// comma-separated value) come from the operator. Until both are valid, <see cref="IsConfigured"/> is false and every
/// verification and registration is refused (fail closed).
/// </summary>
/// <remarks>
/// <c>signin:webauthn:bootstrapPrincipalId</c> names the principal the one-time bootstrap code enrols a first passkey for
/// (doc 32 section 3). <c>signin:webauthn:bootstrapEnabled</c> (default true) can switch the code off. The code only works from loopback.
/// </remarks>
public sealed record WebAuthnOptions
{
    public const int MaxLabelLength = 80;

    public string? RpId { get; init; }

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public Guid? BootstrapPrincipalId { get; init; }

    public bool BootstrapEnabled { get; init; } = true;

    /// <summary>True when the relying-party id and at least one origin are valid and every origin sits under the id.</summary>
    public bool IsConfigured => RpId is not null && AllowedOrigins.Count > 0;

    public bool IsOriginAllowed(string? origin) => origin is not null && AllowedOrigins.Contains(origin, StringComparer.Ordinal);

    public static WebAuthnOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rpId = configuration["signin:rpId"]?.Trim().ToLowerInvariant();
        if (!IsBareHost(rpId))
        {
            return new WebAuthnOptions { BootstrapPrincipalId = ReadPrincipal(configuration), BootstrapEnabled = ReadBootstrapEnabled(configuration) };
        }

        var origins = new List<string>();
        var section = configuration.GetSection("signin:webauthn:origins");
        var raw = section.GetChildren().Select(c => c.Value).Append(section.Value).Where(v => !string.IsNullOrWhiteSpace(v));
        foreach (var value in raw.SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (TryNormalizeOrigin(value, rpId!, out var origin) && !origins.Contains(origin))
            {
                origins.Add(origin);
            }
        }

        return new WebAuthnOptions
        {
            RpId = rpId,
            AllowedOrigins = origins,
            BootstrapPrincipalId = ReadPrincipal(configuration),
            BootstrapEnabled = ReadBootstrapEnabled(configuration),
        };
    }

    private static Guid? ReadPrincipal(IConfiguration configuration) =>
        Guid.TryParse(configuration["signin:webauthn:bootstrapPrincipalId"], out var id) && id != Guid.Empty ? id : null;

    private static bool ReadBootstrapEnabled(IConfiguration configuration) =>
        !bool.TryParse(configuration["signin:webauthn:bootstrapEnabled"], out var enabled) || enabled;

    private static bool IsBareHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && host.Length <= 253
        && !host.Any(c => char.IsWhiteSpace(c) || c is '/' or ':' or '@' or '?' or '#');

    /// <summary>An origin is https (http only for localhost), has no path, query or credentials, and its host is the rp id or a subdomain of it.</summary>
    private static bool TryNormalizeOrigin(string value, string rpId, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || value.EndsWith('/')
            || uri.PathAndQuery != "/"
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        var local = host == "localhost";
        if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        if (host != rpId && !host.EndsWith("." + rpId, StringComparison.Ordinal))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
