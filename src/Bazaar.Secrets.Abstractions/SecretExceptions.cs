namespace Bazaar.Secrets;

/// <summary>The named secret, or its data key, does not exist.</summary>
public sealed class SecretNotFoundException(string message) : Exception(message);

/// <summary>The store refused the operation. <see cref="Reason"/> says why, and never contains a value.</summary>
public sealed class SecretAccessDeniedException : Exception
{
    public SecretAccessDeniedException(string reason)
        : base($"Secret access denied: {reason}.")
    {
        Reason = reason;
    }

    public string Reason { get; }
}
