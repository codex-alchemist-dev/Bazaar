namespace Bazaar.Server.Setup;

/// <summary>
/// One read-only check of a finished setup (doc 45 section 1.12). A check reads the validated plan and reports a status. It never
/// writes, and it never resolves a secret reference (reference resolution is its own check, which needs the Host adapter).
/// </summary>
public interface ISetupVerificationCheck
{
    /// <summary>Stable id, for example <c>network.statusBind</c>. It names the check in the report and in acknowledgements.</summary>
    string Id { get; }

    /// <summary>A short human title, shown next to the id.</summary>
    string Title { get; }

    /// <summary>Runs the check. An exception is caught by the verifier and becomes a red result for this check only.</summary>
    Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What a check may read: the validated <see cref="SetupPlan"/> and nothing else. The plan holds references only, never a secret
/// value, so no check can see one. Build it only from a plan the loader accepted (exit 0 or held).
/// </summary>
public sealed class SetupVerificationContext
{
    public SetupVerificationContext(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
    }

    /// <summary>The validated plan.</summary>
    public SetupPlan Plan { get; }
}
