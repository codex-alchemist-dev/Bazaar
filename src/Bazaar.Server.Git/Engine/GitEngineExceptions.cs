namespace Bazaar.Server.Git;

/// <summary>A per-repo lock or merge lane was not free within <see cref="GitEngineOptions.RepoLockTimeout"/>.</summary>
public sealed class GitLockTimeoutException(string key, TimeSpan timeout)
    : TimeoutException($"Timed out after {timeout} waiting for the git lock '{key}'.");

/// <summary>A git engine call failed or was refused. The message never carries a credential.</summary>
public class GitEngineException : Exception
{
    public GitEngineException(string message)
        : base(message)
    {
    }

    public GitEngineException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
