using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.SignIn.Passkeys;

/// <summary>
/// Relying-party settings for the sign-in page's passkey check (doc 32 section 4). <c>signin:rpId</c> is the registered domain and is
/// required; <c>signin:origins</c> is a comma list of the page origins (default <c>https://&lt;rpId&gt;</c>). Without an RP id no verifier
/// is registered and the deny-all default stays, so a missing setting fails closed.
/// </summary>
public sealed record PasskeyOptions(string RpId, string DisplayName, IReadOnlyList<string> Origins)
{
    /// <summary>Reads the settings, or null when <c>signin:rpId</c> is not set.</summary>
    public static PasskeyOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rp = configuration["signin:rpId"]?.Trim();
        if (string.IsNullOrEmpty(rp))
        {
            return null;
        }

        var origins = (configuration["signin:origins"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (origins.Length == 0)
        {
            origins = [$"https://{rp}"];
        }

        var name = configuration["signin:displayName"];
        return new PasskeyOptions(rp, string.IsNullOrWhiteSpace(name) ? "Bazaar" : name.Trim(), origins);
    }
}
