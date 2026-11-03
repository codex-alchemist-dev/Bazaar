namespace Bazaar.Server.Council.Prompts;

/// <summary>Thrown when a ballot prompt would leak sealed or peer ballot content (docs/design/10-council-and-voting.md, Testing: Privacy).</summary>
public sealed class CouncilPrivacyViolationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the prompt was denied.</param>
    public CouncilPrivacyViolationException(string reason)
        : base($"Council privacy guard denied the prompt: {reason}")
    {
        Reason = reason;
    }

    /// <summary>Why the prompt was denied.</summary>
    public string Reason { get; }
}
