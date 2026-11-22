namespace Bazaar.Server.Walkthrough;

/// <summary>What the server knows the person has done (doc 45 section 2.7). Facts only; never agent or task content.</summary>
/// <param name="CompanyCreated">The install has a company.</param>
/// <param name="FirstAgentName">The first agent's display name, or null when there is none.</param>
/// <param name="FirstTaskGiven">The person has created a task or sent an agent request.</param>
public sealed record WalkthroughServerFacts(bool CompanyCreated, string? FirstAgentName, bool FirstTaskGiven);

/// <summary>One person's saved walkthrough progress, the server's copy of the <c>onboarding.walkthrough</c> preference.</summary>
public sealed record WalkthroughPrefs(bool Dismissed, bool Finished, string Current, IReadOnlyList<string> Completed);

/// <summary>Turns the request's <c>Authorization</c> header into the person it names, or null when the token is missing or not valid.</summary>
public interface IWalkthroughCallerResolver
{
    /// <summary>The person id, or null when the caller is not a signed-in person.</summary>
    Task<string?> ResolveAsync(string? authorizationHeader, CancellationToken cancellationToken = default);
}

/// <summary>Computes the walkthrough facts from the server's own state. The agents and Kanban modules supply the real one.</summary>
public interface IWalkthroughFactsProvider
{
    /// <summary>The facts for one person.</summary>
    Task<WalkthroughServerFacts> ReadAsync(string personId, CancellationToken cancellationToken = default);
}

/// <summary>Per-person storage of the walkthrough preference.</summary>
public interface IWalkthroughPrefsStore
{
    /// <summary>The saved preference, or null when none is saved.</summary>
    Task<WalkthroughPrefs?> GetAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the saved preference.</summary>
    Task SetAsync(string personId, WalkthroughPrefs prefs, CancellationToken cancellationToken = default);
}
