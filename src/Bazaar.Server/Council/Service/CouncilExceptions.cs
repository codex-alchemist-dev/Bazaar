namespace Bazaar.Server.Council.Service;

/// <summary>A Council rule rejected the request (not eligible, wrong state, duplicate, bad choice, and so on).</summary>
public sealed class CouncilRejectedException : Exception
{
    /// <summary>Creates the exception with the rule that was broken.</summary>
    public CouncilRejectedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A vote kept changing under the service: every attempt at a state transition lost to a version mismatch.
/// The caller may retry the whole operation later.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Creates the exception with the vote and the attempts made.</summary>
    public ConcurrencyException(string message)
        : base(message)
    {
    }
}
