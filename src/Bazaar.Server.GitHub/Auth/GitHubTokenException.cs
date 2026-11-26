namespace Bazaar.Server.GitHub.Auth;

/// <summary>
/// A GitHub installation token could not be obtained. The message carries the HTTP status and the Identity Service
/// error code when available. It never contains the token value.
/// </summary>
public sealed class GitHubTokenException : Exception
{
    public GitHubTokenException(string message, int? statusCode = null, string? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>HTTP status returned by the Identity Service, or null when no response arrived.</summary>
    public int? StatusCode { get; }

    /// <summary>Identity Service error code, or null when none was returned.</summary>
    public string? ErrorCode { get; }
}
