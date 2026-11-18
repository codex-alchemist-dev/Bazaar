namespace Bazaar.Server.Setup;

/// <summary>How a setup issue affects the exit code.</summary>
public enum SetupIssueSeverity
{
    /// <summary>The input is wrong. Exit 1.</summary>
    Error,

    /// <summary>The input is valid but blocked by a floor or an approval. Exit 2.</summary>
    Hold,
}

/// <summary>
/// One problem found in a setup plan. <see cref="Path"/> names the key, never a value. <see cref="Message"/> names the rule
/// and never repeats the value the operator wrote, so a mistaken secret cannot leak through an error.
/// </summary>
public sealed record SetupValidationIssue(SetupIssueSeverity Severity, string Path, string Message);
