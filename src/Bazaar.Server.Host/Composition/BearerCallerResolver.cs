using Bazaar.Server.SoulProfile;
using Bazaar.Server.Walkthrough;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Turns an access token into the signed-in human it names. The implementation is the access-token verifier plus the signed-in
/// person lookup that <c>GET /auth/whoami</c> uses (doc 32 section 5, PR #1145); until that lands, the default answers null, so the
/// first-run routes answer 401 to everyone rather than guess. The token is never logged.
/// </summary>
public interface IBearerPersonLookup
{
    /// <summary>The human id for a valid token of a live device and an active human, or null for every other case.</summary>
    Task<Guid?> FindHumanAsync(string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>Knows no one. Replaced by the whoami-backed lookup.</summary>
public sealed class UnavailableBearerPersonLookup : IBearerPersonLookup
{
    public Task<Guid?> FindHumanAsync(string accessToken, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
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
