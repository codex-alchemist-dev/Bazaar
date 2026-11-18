namespace Bazaar.Client.Portability;

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

    /// <summary>
    /// Records the owner's choice for one colliding record in the preview and applies it to the plan (doc 44 section 8.10):
    /// <see cref="CollisionAction.SkipIncoming"/> drops the record from the plan, <see cref="CollisionAction.Replace"/>
    /// keeps it and lists it in <see cref="ImportPreview.ReplaceSourceKeys"/>, and <see cref="CollisionAction.KeepBoth"/>
    /// removes any earlier choice. The mapping hash changes, so any dry-run is cleared. Only in the preview.
    /// </summary>
    public static WizardState ChooseCollisionAction(WizardState state, string sourceKey, CollisionAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(sourceKey);

        if (state.Step != WizardStep.Preview || state.BasePlan?.Preview is null || state.Preview is null)
        {
            return Refuse(state, "collision choices can be made only in the preview");
        }
        var inIncomingCollision = state.Preview.Collisions.Any(c => c.SourceKeys.Contains(sourceKey, StringComparer.Ordinal));
        var existing = state.BasePlan.Preview!.ExistingCollisions.FirstOrDefault(e => string.Equals(e.Incoming.SourceKey, sourceKey, StringComparison.Ordinal));
        if (!inIncomingCollision && existing is null)
        {
            return Refuse(state, "that record is not part of a collision");
        }
        if (action == CollisionAction.Replace && existing is null)
        {
            return Refuse(state, "replace needs an object already stored with the same title");
        }
        var record = state.BasePlan.Preview.New.FirstOrDefault(r => string.Equals(r.SourceKey, sourceKey, StringComparison.Ordinal));
        if (record is null)
        {
            return Refuse(state, "that record is not part of the plan");
        }

        var rest = state.CollisionChoices.Where(d => !string.Equals(d.SourceKey, sourceKey, StringComparison.Ordinal));
        var decisions = (action == CollisionAction.KeepBoth
                ? rest
                : rest.Append(new CollisionDecision(record.Kind, record.Title, sourceKey, action)))
            .OrderBy(d => d.Target, StringComparer.Ordinal)
            .ThenBy(d => d.Title, StringComparer.Ordinal)
            .ThenBy(d => d.SourceKey, StringComparer.Ordinal)
            .ToList();

        var basePlan = state.BasePlan;
        var plan = basePlan with
        {
            Preview = ApplyDecisions(basePlan.Preview!, decisions),
            MappingHash = DecidedMappingHash(basePlan.MappingHash, decisions),
        };
        return state with { CollisionChoices = decisions, Plan = plan, DryRun = null, Notice = null };
    }

    /// <summary>The collision choice for one record, or <see cref="CollisionAction.KeepBoth"/> when none is set.</summary>
    public static CollisionAction ActionFor(WizardState state, string sourceKey) =>
        state.CollisionChoices.FirstOrDefault(d => string.Equals(d.SourceKey, sourceKey, StringComparison.Ordinal))?.Action
        ?? CollisionAction.KeepBoth;

    /// <summary>
    /// Feeds the host's lookup of stored objects back into the preview (doc 44 section 8.10). Each incoming record with a
    /// stored object of the same kind and title is listed as a collision that may be replaced. A replace choice that no
    /// longer matches a stored object is dropped; the plan and its mapping hash are rebuilt, so any dry-run is cleared.
    /// </summary>
    public static WizardState WithExistingObjects(WizardState state, IReadOnlyList<ExistingObject> existing)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(existing);

        if (state.Step != WizardStep.Preview || state.BasePlan?.Preview is null)
        {
            return Refuse(state, "existing objects can be checked only in the preview");
        }

        var basePreview = state.BasePlan.Preview with { ExistingCollisions = ExistingCollisions.Find(state.BasePlan.Preview, existing) };
        var stillCollides = basePreview.ExistingCollisions.Select(c => c.Incoming.SourceKey).ToHashSet(StringComparer.Ordinal);
        var decisions = state.CollisionChoices
            .Where(d => d.Action != CollisionAction.Replace || stillCollides.Contains(d.SourceKey))
            .ToList();

        var basePlan = state.BasePlan with { Preview = basePreview };
        var plan = basePlan with
        {
            Preview = ApplyDecisions(basePreview, decisions),
            MappingHash = DecidedMappingHash(basePlan.MappingHash, decisions),
        };
        return state with
        {
            BasePlan = basePlan,
            Plan = plan,
            Preview = PreviewSummary.From(basePreview),
            CollisionChoices = decisions,
            DryRun = null,
            Notice = null,
        };
    }

    /// <summary>
    /// Failed -> the step the failure came from. A failed dry-run or commit goes back to the preview with the same
    /// plan and choices; a failed detection or planning run is run again from the source.
    /// </summary>
    public WizardState Retry(WizardState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Step != WizardStep.Failed)
        {
            return Refuse(state, "retry is allowed only after a failure");
        }
        if (state.Plan is not null)
        {
            return state with { Step = WizardStep.Preview, DryRun = null, Commit = null, Message = null, Notice = null };
        }
        if (state.Source is not null)
        {
            var result = _pipeline.Run(state.Source, state.CommittedKeys);
            return ApplyPipeline(state with { Message = null, Notice = null }, result);
        }
        return Refuse(state, "there is nothing to retry");
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
                    BasePlan = result,
                    Preview = PreviewSummary.From(result.Preview!),
                    DryRun = null,
                    CollisionChoices = [],
                    Message = null,
                    Notice = null,
                };
            case ImportStatus.Ambiguous:
                return state with
                {
                    Step = WizardStep.Detect,
                    Candidates = result.Candidates,
                    Plan = null,
                    BasePlan = null,
                    Preview = null,
                    DryRun = null,
                    Notice = null,
                };
            default:
                return Fail(state with { Candidates = result.Candidates, Plan = null, BasePlan = null, Preview = null, DryRun = null }, result.Message ?? "import failed");
        }
    }

    /// <summary>The preview with the skipped records removed and the replaced ones listed.</summary>
    private static ImportPreview ApplyDecisions(ImportPreview preview, List<CollisionDecision> decisions)
    {
        var skipped = decisions.Where(d => d.Action == CollisionAction.SkipIncoming).Select(d => d.SourceKey).ToHashSet(StringComparer.Ordinal);
        var kept = preview.New.Where(r => !skipped.Contains(r.SourceKey)).ToList();
        var keptKeys = kept.Select(r => r.SourceKey).ToHashSet(StringComparer.Ordinal);
        var replaced = decisions
            .Where(d => d.Action == CollisionAction.Replace && keptKeys.Contains(d.SourceKey))
            .Select(d => d.SourceKey)
            .ToList();
        return preview with { New = kept, ReplaceSourceKeys = replaced };
    }

    /// <summary>The plan's mapping hash with the collision choices folded in. No choices keeps the original hash.</summary>
    private static string DecidedMappingHash(string mappingHash, List<CollisionDecision> decisions)
    {
        if (decisions.Count == 0)
        {
            return mappingHash;
        }
        var text = mappingHash + "\n" + string.Join("\n", decisions.Select(d => $"{d.SourceKey}\u001f{d.Action}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
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
