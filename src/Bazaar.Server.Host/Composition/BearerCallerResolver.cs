using Bazaar.Server.Components.Remote;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.SoulProfile;
using Bazaar.Server.Walkthrough;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Turns an access token into the signed-in human it names: <see cref="WhoAmIBearerPersonLookup"/> is the access-token verifier plus
/// the signed-in person lookup that <c>GET /auth/whoami</c> uses (doc 32 section 5). With no verifier registered the default answers
/// null, so the first-run routes answer 401 to everyone rather than guess. The token is never logged.
/// </summary>
public interface IBearerPersonLookup
{
    /// <summary>The human id for a valid token of a live device and an active human, or null for every other case.</summary>
    Task<Guid?> FindHumanAsync(string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>Knows no one. Used when the host has no access-token verifier.</summary>
public sealed class UnavailableBearerPersonLookup : IBearerPersonLookup
{
    public Task<Guid?> FindHumanAsync(string accessToken, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
}

/// <summary>
/// The whoami check: a valid signature and expiry, then a live device and an active human, so revocation and removal are immediate.
/// Every refusal is null; the caller never learns which one applied.
/// </summary>
public sealed class WhoAmIBearerPersonLookup(IAccessTokenVerifier verifier, SignedInPersonLookup people) : IBearerPersonLookup
{
    public async Task<Guid?> FindHumanAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (!verifier.TryVerify(accessToken, out var caller)) return null;
        var person = await people.FindAsync(caller.DeviceId, caller.HumanId, cancellationToken).ConfigureAwait(false);
        return person?.HumanId;
    }
}

/// <summary>
/// Resolves the <c>Authorization</c> header of a first-run route to a person id: a <c>Bearer</c> token, looked up through
/// <see cref="IBearerPersonLookup"/>. The person id is the human id in its canonical text form, which is what the roles checker takes.
/// </summary>
public sealed class BearerCallerResolver(IBearerPersonLookup lookup) : IWalkthroughCallerResolver, ISoulProfileCallerResolver
{
    private const string _scheme = "Bearer ";

    public async Task<string?> ResolveAsync(string? authorizationHeader, CancellationToken cancellationToken = default)
    {
        var token = TokenOf(authorizationHeader);
        if (token is null) return null;
        return await lookup.FindHumanAsync(token, cancellationToken).ConfigureAwait(false) is { } human ? human.ToString() : null;
    }

    /// <summary>The token of a <c>Bearer</c> header, or null when the header is missing, another scheme, or has no token.</summary>
    internal static string? TokenOf(string? header)
    {
        if (header is null || !header.StartsWith(_scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[_scheme.Length..].Trim();
        return token.Length == 0 || token.Contains(' ', StringComparison.Ordinal) ? null : token;
    }
}
