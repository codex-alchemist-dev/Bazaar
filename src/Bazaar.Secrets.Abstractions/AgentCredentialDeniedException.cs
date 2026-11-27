namespace Bazaar.Secrets;

/// <summary>Raised when a credential is refused, for example because the agent is Quarantined or Revoked. It is an <see cref="InvalidOperationException"/>, matching the seam's contract.</summary>
public sealed class AgentCredentialDeniedException(string message) : InvalidOperationException(message);
