namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>Base failure for the GitHub App token path. Messages never contain tokens or private keys.</summary>
public class GitHubAppException : InvalidOperationException
{
    /// <summary>Creates the exception with a message that names no secret.</summary>
    /// <param name="message">A message that names no token or key.</param>
    public GitHubAppException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause that names no secret.</summary>
    /// <param name="message">A message that names no token or key.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GitHubAppException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No GitHub App is registered for the agent. Thrown by key stores; the PEM is never part of it.</summary>
public sealed class GitHubAppKeyNotFoundException : GitHubAppException
{
    /// <summary>Creates the exception for one agent.</summary>
    /// <param name="agentId">The agent that has no App.</param>
    public GitHubAppKeyNotFoundException(string agentId)
        : base($"No GitHub App is registered for agent '{agentId}'.")
    {
    }
}

/// <summary>A requested repository falls outside the agent's lease scope, or the lease has no repositories.</summary>
public sealed class GitHubAppScopeException : GitHubAppException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was refused. Names the agent and repository, never a token.</param>
    public GitHubAppScopeException(string message)
        : base(message)
    {
    }
}
