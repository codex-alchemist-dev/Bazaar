using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.SignIn;

/// <summary>
/// Sign-in settings. <c>signin:enabled</c> turns on the native handoff (off by default, doc 32 section 4). <c>signin:adminEnabled</c>
/// (default true) keeps the page reachable for the admin panel, which is on by default (doc 51). The routes are served when either is on.
/// </summary>
public sealed record SignInOptions(bool Enabled, bool AdminEnabled, string DisplayName, string? RpId, bool AllowPlaintextOverlay = false)
{
    public const string DefaultDisplayName = "Bazaar server";

    public static SignInOptions Default { get; } = new(false, true, DefaultDisplayName, null);

    public bool Served => Enabled || AdminEnabled;

    /// <summary>Whether a handoff code may be issued for <paramref name="audience"/>.</summary>
    public bool Allows(string? audience) => audience switch
    {
        SignInAudiences.Admin => AdminEnabled,
        SignInAudiences.Native => Enabled,
        _ => false,
    };

    public static SignInOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var name = configuration["signin:displayName"];
        var rp = configuration["signin:rpId"];
        // The same setting the wire and refresh routes read (wire.allow_plaintext_overlay), so one switch governs every credential route.
        var plaintext = bool.TryParse(configuration["Bazaar:Wire:AllowPlaintextOverlay"], out var p) && p;
        return new SignInOptions(
            bool.TryParse(configuration["signin:enabled"], out var e) && e,
            !bool.TryParse(configuration["signin:adminEnabled"], out var a) || a,
            string.IsNullOrWhiteSpace(name) || name.Length > 80 ? DefaultDisplayName : name.Trim(),
            string.IsNullOrWhiteSpace(rp) ? null : rp.Trim(),
            plaintext);
    }
}
