namespace Bazaar.Server.Setup;

/// <summary>Exit codes of the setup commands (doc 45 section 1.7).</summary>
public enum SetupExitCode
{
    /// <summary>The input is valid, or the plan was applied.</summary>
    Ok = 0,

    /// <summary>The input is invalid: a schema, type, reference, or validation error.</summary>
    InvalidInput = 1,

    /// <summary>Blocked by a floor, or by a change that needs approval. The change is held, not refused.</summary>
    Held = 2,

    /// <summary>An apply step failed and was rolled back.</summary>
    ApplyFailedRolledBack = 3,
}

/// <summary>Maps validation issues and apply outcomes to exit codes.</summary>
public static class SetupExitCodes
{
    /// <summary>
    /// Invalid input (1) wins over held (2), so a file with both an error and a hold exits 1. No issues exits 0.
    /// Holds alone exit 2.
    /// </summary>
    public static SetupExitCode ForIssues(IEnumerable<SetupValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var list = issues.ToList();
        if (list.Any(issue => issue.Severity == SetupIssueSeverity.Error))
            return SetupExitCode.InvalidInput;
        return list.Any(issue => issue.Severity == SetupIssueSeverity.Hold) ? SetupExitCode.Held : SetupExitCode.Ok;
    }
}
