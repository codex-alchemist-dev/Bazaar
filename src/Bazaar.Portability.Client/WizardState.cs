namespace Bazaar.Portability.Client;

/// <summary>The wizard steps (doc 44 section 8.1). Done and Failed are terminal.</summary>
public enum WizardStep
{
    ChooseSource,
    Detect,
    Preview,
    DryRun,
    Commit,
    Done,
    Failed,
}

/// <summary>
/// Immutable snapshot of the import wizard. Each transition in <see cref="ImportWizard"/> returns a new one.
/// <see cref="Notice"/> holds the reason the last request was refused; the step is unchanged in that case.
/// </summary>
public sealed record WizardState
{
    private static readonly IReadOnlySet<string> _noKeys = new HashSet<string>(StringComparer.Ordinal);

    public static WizardState Initial { get; } = new();

    public WizardStep Step { get; init; } = WizardStep.ChooseSource;

    public ImportSource? Source { get; init; }

    public IReadOnlySet<string> CommittedKeys { get; init; } = _noKeys;

    /// <summary>Importers that offered to handle the source. Set by detection, kept for the Detect step.</summary>
    public IReadOnlyList<ImportCandidate> Candidates { get; init; } = [];

    /// <summary>The pipeline result for the current mapping. Null until a single importer is chosen.</summary>
    public ImportPipelineResult? Plan { get; init; }

    public PreviewSummary? Preview { get; init; }

    /// <summary>The dry-run for the current mapping. Null until the dry-run runs; cleared by any mapping change.</summary>
    public ImportDryRunReport? DryRun { get; init; }

    public ImportCommitReport? Commit { get; init; }

    /// <summary>Why the wizard failed. Set only in the Failed step.</summary>
    public string? Message { get; init; }

    /// <summary>Why the last request was refused. Cleared by every accepted transition.</summary>
    public string? Notice { get; init; }

    /// <summary>
    /// True only in the DryRun step, when the dry-run succeeded and its mapping hash matches the current mapping.
    /// This is the same rule <see cref="ImportPipeline.CanCommit"/> applies.
    /// </summary>
    public bool CanCommit =>
        Step == WizardStep.DryRun
        && Plan is not null
        && DryRun is { Succeeded: true }
        && ImportPipeline.CanCommit(Plan with { MappingHash = DryRun.MappingHash }, Plan.MappingHash);
}
