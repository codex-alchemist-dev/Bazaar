namespace Bazaar.Portability.Client;

/// <summary>
/// Pure state machine for the client import wizard. Each transition returns a new <see cref="WizardState"/>.
/// No I/O: dry-run and commit reports come from the host (see <see cref="IImportCommitter"/>).
/// A request that is not allowed in the current step returns the same step with <see cref="WizardState.Notice"/> set.
/// </summary>
public sealed class ImportWizard
{
    private readonly ImportPipeline _pipeline;

    public ImportWizard(ImportPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    /// <summary>Choose source -> Detect, Preview or Failed, depending on what detection finds.</summary>
    public WizardState ChooseSource(WizardState state, ImportSource source, IReadOnlySet<string> committedKeys)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(committedKeys);

        if (state.Step != WizardStep.ChooseSource)
        {
            return Refuse(state, "a source is already chosen");
        }

        var result = _pipeline.Run(source, committedKeys);
        var next = state with { Source = source, CommittedKeys = committedKeys };
        return ApplyPipeline(next, result);
    }

    /// <summary>
    /// Picks an importer. In Detect this resolves an ambiguous source (the importer must be a candidate).
    /// In Preview or DryRun it is a mapping change: mapping is re-run and any dry-run is invalidated.
    /// </summary>
    public WizardState ChooseImporter(WizardState state, string importerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(importerId);

        if (state.Step is not (WizardStep.Detect or WizardStep.Preview or WizardStep.DryRun) || state.Source is null)
        {
            return Refuse(state, "an importer can be chosen only while detecting, previewing or dry-running");
        }
        if (state.Step == WizardStep.Detect && !state.Candidates.Any(c => string.Equals(c.Id, importerId, StringComparison.Ordinal)))
        {
            return Refuse(state, "the importer is not one of the candidates");
        }

        var result = _pipeline.Run(state.Source, state.CommittedKeys, importerId);
        if (result.Status == ImportStatus.Unrecognised)
        {
            return Refuse(state, result.Message ?? "importer not available");
        }
        return ApplyPipeline(state, result);
    }

    /// <summary>Preview -> DryRun. The dry-run itself runs on the host and is reported back with <see cref="CompleteDryRun"/>.</summary>
    public static WizardState StartDryRun(WizardState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Step != WizardStep.Preview)
        {
            return Refuse(state, "a dry-run can start only from the preview");
        }
        return state with { Step = WizardStep.DryRun, DryRun = null, Notice = null };
    }

    /// <summary>Records the dry-run report. A failed dry-run ends the wizard in Failed.</summary>
    public static WizardState CompleteDryRun(WizardState state, ImportDryRunReport report)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(report);

        if (state.Step != WizardStep.DryRun || state.DryRun is not null)
        {
            return Refuse(state, "no dry-run is pending");
        }
        if (!report.Succeeded)
        {
            return Fail(state with { DryRun = report }, report.Message ?? "dry-run failed");
        }
        return state with { DryRun = report, Notice = null };
    }

    /// <summary>DryRun -> Commit. Refused unless the dry-run matches the current mapping hash.</summary>
    public static WizardState StartCommit(WizardState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.CanCommit)
        {
            return Refuse(state, CommitRefusalReason(state));
        }
        return state with { Step = WizardStep.Commit, Notice = null };
    }

    /// <summary>Records the commit report. Commit -> Done on success, Commit -> Failed otherwise.</summary>
    public static WizardState CompleteCommit(WizardState state, ImportCommitReport report)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(report);

        if (state.Step != WizardStep.Commit || state.Commit is not null)
        {
            return Refuse(state, "no commit is pending");
        }
        if (!report.Succeeded)
        {
            return Fail(state with { Commit = report }, report.Message ?? "commit failed");
        }
        return state with { Step = WizardStep.Done, Commit = report, Notice = null };
    }

    private static WizardState ApplyPipeline(WizardState state, ImportPipelineResult result)
    {
        switch (result.Status)
        {
            case ImportStatus.Ready:
                return state with
                {
                    Step = WizardStep.Preview,
                    Candidates = result.Candidates,
                    Plan = result,
                    Preview = PreviewSummary.From(result.Preview!),
                    DryRun = null,
                    Message = null,
                    Notice = null,
                };
            case ImportStatus.Ambiguous:
                return state with
                {
                    Step = WizardStep.Detect,
                    Candidates = result.Candidates,
                    Plan = null,
                    Preview = null,
                    DryRun = null,
                    Notice = null,
                };
            default:
                return Fail(state with { Candidates = result.Candidates, Plan = null, Preview = null, DryRun = null }, result.Message ?? "import failed");
        }
    }

    private static WizardState Fail(WizardState state, string message) =>
        state with { Step = WizardStep.Failed, Message = message, Notice = null };

    private static WizardState Refuse(WizardState state, string reason) => state with { Notice = reason };

    private static string CommitRefusalReason(WizardState state)
    {
        if (state.Step != WizardStep.DryRun)
        {
            return "commit is allowed only from the dry-run step";
        }
        if (state.DryRun is null)
        {
            return "commit needs a dry-run result";
        }
        if (!state.DryRun.Succeeded)
        {
            return "the dry-run did not succeed";
        }
        return "the dry-run does not match the current mapping; run it again";
    }
}
