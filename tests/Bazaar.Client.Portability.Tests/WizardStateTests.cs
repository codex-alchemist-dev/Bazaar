using System.Text;

namespace Bazaar.Client.Portability.Tests;

public class WizardStateTests
{
    private static readonly IReadOnlySet<string> _noKeys = new HashSet<string>(StringComparer.Ordinal);

    private static ImportSource Src(string text = "hello") => new("notes.txt", Encoding.UTF8.GetBytes(text));

    private static SourceRecord Record(string key, string kind = "document", string title = "") =>
        new(key, kind, title.Length == 0 ? key : title, "body", new Dictionary<string, string>());

    private sealed class FakeImporter(string id, double confidence, Func<IReadOnlyList<SourceRecord>>? parse = null, string version = "1.0.0") : IPortabilityImporter
    {
        public string Id => id;
        public string Version => version;
        public DetectResult Detect(ImportSource source) => new(confidence, id + " reason");
        public IReadOnlyList<SourceRecord> Parse(ImportSource source) =>
            parse is null ? [Record(id + "-1")] : parse();
    }

    private static ImportWizard Wizard(params IPortabilityImporter[] importers) => new(new ImportPipeline(importers));

    private static ImportWizard SingleImporterWizard() =>
        Wizard(new FakeImporter("bazaar.import.a", 0.9, () => [Record("k1"), Record("k2")]));

    /// <summary>Drives a wizard from ChooseSource to the Preview step with one clear importer.</summary>
    private static WizardState AtPreview(ImportWizard wizard) => wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);

    private static ImportDryRunReport OkDryRun(WizardState state) =>
        new(true, state.Plan!.MappingHash, WouldWrite: state.Preview!.NewCount, WouldConflict: 0);

    private static WizardState AtCommittable(ImportWizard wizard)
    {
        var state = ImportWizard.StartDryRun(AtPreview(wizard));
        return ImportWizard.CompleteDryRun(state, OkDryRun(state));
    }

    [Fact]
    public void Initial_state_is_choose_source_with_nothing_chosen()
    {
        var state = WizardState.Initial;

        Assert.Equal(WizardStep.ChooseSource, state.Step);
        Assert.Null(state.Source);
        Assert.Null(state.Plan);
        Assert.False(state.CanCommit);
    }

    [Fact]
    public void Choose_source_with_one_clear_importer_moves_to_preview()
    {
        var wizard = SingleImporterWizard();

        var state = AtPreview(wizard);

        Assert.Equal(WizardStep.Preview, state.Step);
        Assert.NotNull(state.Source);
        Assert.Equal("bazaar.import.a", state.Plan!.ImporterId);
        Assert.Equal(2, state.Preview!.NewCount);
        Assert.Null(state.Notice);
        Assert.Null(state.DryRun);
    }

    [Fact]
    public void Choose_source_with_ambiguous_candidates_moves_to_detect()
    {
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.8), new FakeImporter("bazaar.import.b", 0.75));

        var state = wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);

        Assert.Equal(WizardStep.Detect, state.Step);
        Assert.Equal(2, state.Candidates.Count);
        Assert.Null(state.Plan);
        Assert.Null(state.Preview);
    }

    [Fact]
    public void Choose_source_with_no_importer_fails_with_message()
    {
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.0));

        var state = wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);

        Assert.Equal(WizardStep.Failed, state.Step);
        Assert.Equal("no importer recognised this source", state.Message);
    }

    [Fact]
    public void Parse_error_fails_the_wizard()
    {
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.9, () => throw new InvalidDataException("bad zip")));

        var state = AtPreview(wizard);

        Assert.Equal(WizardStep.Failed, state.Step);
        Assert.Equal("bad zip", state.Message);
    }

    [Fact]
    public void Choose_source_twice_is_refused_and_leaves_the_step_unchanged()
    {
        var wizard = SingleImporterWizard();
        var preview = AtPreview(wizard);

        var refused = wizard.ChooseSource(preview, Src("other"), _noKeys);

        Assert.Equal(WizardStep.Preview, refused.Step);
        Assert.Same(preview.Plan, refused.Plan);
        Assert.NotNull(refused.Notice);
    }

    [Fact]
    public void Detect_step_accepts_a_candidate_and_moves_to_preview()
    {
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.8, () => [Record("a1")]), new FakeImporter("bazaar.import.b", 0.75, () => [Record("b1")]));
        var detect = wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);

        var state = wizard.ChooseImporter(detect, "bazaar.import.b");

        Assert.Equal(WizardStep.Preview, state.Step);
        Assert.Equal("bazaar.import.b", state.Plan!.ImporterId);
        Assert.Null(state.Notice);
    }

    [Fact]
    public void Detect_step_refuses_an_importer_that_is_not_a_candidate()
    {
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.8), new FakeImporter("bazaar.import.b", 0.75), new FakeImporter("bazaar.import.c", 0.0));
        var detect = wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);

        var state = wizard.ChooseImporter(detect, "bazaar.import.c");

        Assert.Equal(WizardStep.Detect, state.Step);
        Assert.Null(state.Plan);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Start_dry_run_moves_from_preview_to_dry_run_without_a_report()
    {
        var wizard = SingleImporterWizard();

        var state = ImportWizard.StartDryRun(AtPreview(wizard));

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.Null(state.DryRun);
        Assert.False(state.CanCommit);
    }

    [Fact]
    public void Start_dry_run_is_refused_outside_preview()
    {
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.8), new FakeImporter("bazaar.import.b", 0.75));
        var detect = wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);

        var state = ImportWizard.StartDryRun(detect);

        Assert.Equal(WizardStep.Detect, state.Step);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Successful_dry_run_report_makes_commit_possible()
    {
        var wizard = SingleImporterWizard();

        var state = AtCommittable(wizard);

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.NotNull(state.DryRun);
        Assert.True(state.CanCommit);
    }

    [Fact]
    public void Failed_dry_run_ends_in_failed_with_its_message()
    {
        var wizard = SingleImporterWizard();
        var dryRunning = ImportWizard.StartDryRun(AtPreview(wizard));

        var state = ImportWizard.CompleteDryRun(dryRunning, new ImportDryRunReport(false, dryRunning.Plan!.MappingHash, 0, 0, "scratch target full"));

        Assert.Equal(WizardStep.Failed, state.Step);
        Assert.Equal("scratch target full", state.Message);
        Assert.False(state.CanCommit);
    }

    [Fact]
    public void Dry_run_report_is_accepted_only_once()
    {
        var wizard = SingleImporterWizard();
        var committable = AtCommittable(wizard);

        var state = ImportWizard.CompleteDryRun(committable, OkDryRun(committable));

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.NotNull(state.Notice);
        Assert.True(state.CanCommit);
    }

    [Fact]
    public void Commit_is_refused_in_preview_without_a_dry_run()
    {
        var wizard = SingleImporterWizard();
        var preview = AtPreview(wizard);

        var state = ImportWizard.StartCommit(preview);

        Assert.Equal(WizardStep.Preview, state.Step);
        Assert.NotNull(state.Notice);
        Assert.Null(state.Commit);
    }

    [Fact]
    public void Commit_is_refused_while_the_dry_run_is_pending()
    {
        var wizard = SingleImporterWizard();
        var pending = ImportWizard.StartDryRun(AtPreview(wizard));

        var state = ImportWizard.StartCommit(pending);

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Commit_is_refused_when_the_dry_run_hash_does_not_match_the_mapping()
    {
        var wizard = SingleImporterWizard();
        var dryRunning = ImportWizard.StartDryRun(AtPreview(wizard));
        var stale = ImportWizard.CompleteDryRun(dryRunning, new ImportDryRunReport(true, "not-the-current-hash", 2, 0));

        var state = ImportWizard.StartCommit(stale);

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.False(state.CanCommit);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Commit_is_refused_after_a_failed_dry_run_report()
    {
        var wizard = SingleImporterWizard();
        var dryRunning = ImportWizard.StartDryRun(AtPreview(wizard));
        var failed = new ImportDryRunReport(false, dryRunning.Plan!.MappingHash, 0, 0);
        var dryRun = dryRunning with { DryRun = failed };

        var state = ImportWizard.StartCommit(dryRun);

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Matching_dry_run_allows_commit_and_commit_success_is_done()
    {
        var wizard = SingleImporterWizard();

        var committing = ImportWizard.StartCommit(AtCommittable(wizard));
        var state = ImportWizard.CompleteCommit(committing, new ImportCommitReport(true, 2));

        Assert.Equal(WizardStep.Commit, committing.Step);
        Assert.Equal(WizardStep.Done, state.Step);
        Assert.Equal(2, state.Commit!.Written);
        Assert.Null(state.Notice);
    }

    [Fact]
    public void Commit_failure_ends_in_failed_with_its_message()
    {
        var wizard = SingleImporterWizard();
        var committing = ImportWizard.StartCommit(AtCommittable(wizard));

        var state = ImportWizard.CompleteCommit(committing, new ImportCommitReport(false, 0, "batch 1 rolled back"));

        Assert.Equal(WizardStep.Failed, state.Step);
        Assert.Equal("batch 1 rolled back", state.Message);
    }

    [Fact]
    public void Complete_commit_is_refused_outside_the_commit_step()
    {
        var wizard = SingleImporterWizard();
        var committable = AtCommittable(wizard);

        var state = ImportWizard.CompleteCommit(committable, new ImportCommitReport(true, 2));

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Mapping_change_in_preview_re_runs_mapping_and_changes_the_hash()
    {
        var wizard = Wizard(
            new FakeImporter("bazaar.import.a", 0.9, () => [Record("k1")]),
            new FakeImporter("bazaar.import.b", 0.1, () => [Record("x1"), Record("x2")]));
        var preview = AtPreview(wizard);

        var state = wizard.ChooseImporter(preview, "bazaar.import.b");

        Assert.Equal(WizardStep.Preview, state.Step);
        Assert.Equal("bazaar.import.b", state.Plan!.ImporterId);
        Assert.NotEqual(preview.Plan!.MappingHash, state.Plan.MappingHash);
        Assert.Equal(2, state.Preview!.NewCount);
    }

    [Fact]
    public void Mapping_change_invalidates_a_matching_dry_run()
    {
        var wizard = Wizard(
            new FakeImporter("bazaar.import.a", 0.9, () => [Record("k1")]),
            new FakeImporter("bazaar.import.b", 0.1, () => [Record("x1")]));
        var committable = AtCommittable(wizard);
        Assert.True(committable.CanCommit);

        var changed = wizard.ChooseImporter(committable, "bazaar.import.b");

        Assert.Equal(WizardStep.Preview, changed.Step);
        Assert.Null(changed.DryRun);
        Assert.False(changed.CanCommit);
        Assert.Null(changed.Notice);
    }

    [Fact]
    public void After_a_mapping_change_the_old_dry_run_cannot_commit_even_if_re_attached()
    {
        var wizard = Wizard(
            new FakeImporter("bazaar.import.a", 0.9, () => [Record("k1")]),
            new FakeImporter("bazaar.import.b", 0.1, () => [Record("x1")]));
        var oldDryRun = AtCommittable(wizard).DryRun!;
        var changed = wizard.ChooseImporter(AtCommittable(wizard), "bazaar.import.b");

        var reattached = ImportWizard.StartCommit(changed with { Step = WizardStep.DryRun, DryRun = oldDryRun });

        Assert.Equal(WizardStep.DryRun, reattached.Step);
        Assert.False(reattached.CanCommit);
        Assert.NotNull(reattached.Notice);
    }

    [Fact]
    public void Mapping_change_in_dry_run_returns_to_preview_and_requires_a_new_dry_run()
    {
        var wizard = Wizard(
            new FakeImporter("bazaar.import.a", 0.9, () => [Record("k1")]),
            new FakeImporter("bazaar.import.b", 0.1, () => [Record("x1")]));
        var dryRunning = ImportWizard.StartDryRun(AtPreview(wizard));

        var changed = wizard.ChooseImporter(dryRunning, "bazaar.import.b");
        var recheck = ImportWizard.StartCommit(changed);
        var rerun = ImportWizard.CompleteDryRun(ImportWizard.StartDryRun(changed), new ImportDryRunReport(true, changed.Plan!.MappingHash, 1, 0));

        Assert.Equal(WizardStep.Preview, changed.Step);
        Assert.Equal(WizardStep.Preview, recheck.Step);
        Assert.NotNull(recheck.Notice);
        Assert.True(rerun.CanCommit);
    }

    [Fact]
    public void Mapping_change_to_an_unregistered_importer_is_refused_and_keeps_the_dry_run()
    {
        var wizard = SingleImporterWizard();
        var committable = AtCommittable(wizard);

        var state = wizard.ChooseImporter(committable, "bazaar.import.missing");

        Assert.Equal(WizardStep.DryRun, state.Step);
        Assert.NotNull(state.Notice);
        Assert.True(state.CanCommit);
    }

    [Fact]
    public void Requests_after_done_are_refused()
    {
        var wizard = SingleImporterWizard();
        var done = ImportWizard.CompleteCommit(ImportWizard.StartCommit(AtCommittable(wizard)), new ImportCommitReport(true, 2));

        var state = wizard.ChooseImporter(done, "bazaar.import.a");

        Assert.Equal(WizardStep.Done, state.Step);
        Assert.NotNull(state.Notice);
    }

    [Fact]
    public void Transitions_do_not_mutate_the_previous_state()
    {
        var wizard = SingleImporterWizard();
        var preview = AtPreview(wizard);

        _ = ImportWizard.StartDryRun(preview);

        Assert.Equal(WizardStep.Preview, preview.Step);
        Assert.Null(preview.DryRun);
    }

    [Fact]
    public void Preview_samples_are_capped_per_target()
    {
        var records = Enumerable.Range(1, 25).Select(i => Record("d" + i, "document", "doc " + i)).ToList();
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.9, () => records));

        var state = AtPreview(wizard);

        var target = Assert.Single(state.Preview!.Targets);
        Assert.Equal("document", target.Target);
        Assert.Equal(25, target.Count);
        Assert.Equal(PreviewSummary.SampleLimit, target.Samples.Count);
    }

    [Fact]
    public void Preview_reports_collisions_unmapped_items_and_duplicates()
    {
        var records = new List<SourceRecord>
        {
            Record("c1", "card", "Same title"),
            Record("c2", "card", "Same title"),
            Record("c3", "card", "Other"),
            new("u1", "", "no kind", "body", new Dictionary<string, string>()),
            new("u2", "card", "", "body", new Dictionary<string, string>()),
            Record("old", "card", "Already here"),
        };
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.9, () => records));
        var committed = new HashSet<string>(["old"], StringComparer.Ordinal);

        var state = wizard.ChooseSource(WizardState.Initial, Src(), committed);

        var summary = state.Preview!;
        Assert.Equal(5, summary.NewCount);
        Assert.Equal(1, summary.DuplicateCount);
        var collision = Assert.Single(summary.Collisions);
        Assert.Equal("card", collision.Target);
        Assert.Equal("Same title", collision.Title);
        Assert.Equal(["c1", "c2"], collision.SourceKeys);
        Assert.Equal(2, summary.Unmapped.Count);
        Assert.Equal("no kind", summary.Unmapped[0].Reason);
        Assert.Equal("no title", summary.Unmapped[1].Reason);
    }

    private static WizardState AtCollisionPreview(out ImportWizard wizard)
    {
        var records = new List<SourceRecord>
        {
            Record("c1", "card", "Same title"),
            Record("c2", "card", "Same title"),
            Record("c3", "card", "Other"),
        };
        wizard = Wizard(new FakeImporter("bazaar.import.a", 0.9, () => records));
        return wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);
    }

    /// <summary>The same preview with a stored object for the colliding title, so replace is allowed.</summary>
    private static WizardState WithStored(WizardState state) =>
        ImportWizard.WithExistingObjects(state, [new ExistingObject("card", "Same title", "obj-1", "rev-7")]);

    [Fact]
    public void Collisions_keep_every_record_by_default()
    {
        var state = AtCollisionPreview(out _);

        Assert.Empty(state.CollisionChoices);
        Assert.Equal(["c1", "c2", "c3"], state.Plan!.Preview!.New.Select(r => r.SourceKey).ToList());
        Assert.Empty(state.Plan.Preview.ReplaceSourceKeys);
        Assert.Equal(CollisionAction.KeepBoth, ImportWizard.ActionFor(state, "c1"));
    }

    [Fact]
    public void Skip_incoming_drops_that_record_from_the_plan()
    {
        var state = AtCollisionPreview(out _);
        var originalHash = state.Plan!.MappingHash;

        var chosen = ImportWizard.ChooseCollisionAction(state, "c2", CollisionAction.SkipIncoming);

        Assert.Equal(WizardStep.Preview, chosen.Step);
        Assert.Null(chosen.Notice);
        var decision = Assert.Single(chosen.CollisionChoices);
        Assert.Equal(CollisionAction.SkipIncoming, decision.Action);
        Assert.Equal(["c1", "c3"], chosen.Plan!.Preview!.New.Select(r => r.SourceKey).ToList());
        Assert.NotEqual(originalHash, chosen.Plan.MappingHash);
        // The preview still lists the collision so the choice stays visible.
        Assert.Single(chosen.Preview!.Collisions);
    }

    [Fact]
    public void Replace_keeps_the_record_and_lists_it_for_the_audited_write()
    {
        var state = AtCollisionPreview(out _);

        var chosen = ImportWizard.ChooseCollisionAction(WithStored(state), "c1", CollisionAction.Replace);

        Assert.Null(chosen.Notice);
        Assert.Equal(["c1", "c2", "c3"], chosen.Plan!.Preview!.New.Select(r => r.SourceKey).ToList());
        Assert.Equal(["c1"], chosen.Plan.Preview.ReplaceSourceKeys);
        Assert.Equal(CollisionAction.Replace, Assert.Single(chosen.CollisionChoices).Action);
    }

    [Fact]
    public void Keep_both_removes_an_earlier_choice_and_restores_the_original_plan()
    {
        var state = AtCollisionPreview(out _);
        var originalHash = state.Plan!.MappingHash;

        var skipped = ImportWizard.ChooseCollisionAction(state, "c2", CollisionAction.SkipIncoming);
        var restored = ImportWizard.ChooseCollisionAction(skipped, "c2", CollisionAction.KeepBoth);

        Assert.Empty(restored.CollisionChoices);
        Assert.Equal(["c1", "c2", "c3"], restored.Plan!.Preview!.New.Select(r => r.SourceKey).ToList());
        Assert.Equal(originalHash, restored.Plan.MappingHash);
    }

    [Fact]
    public void Each_record_in_a_collision_has_its_own_choice()
    {
        var state = AtCollisionPreview(out _);

        var chosen = ImportWizard.ChooseCollisionAction(WithStored(state), "c1", CollisionAction.Replace);
        chosen = ImportWizard.ChooseCollisionAction(chosen, "c2", CollisionAction.SkipIncoming);

        Assert.Equal(CollisionAction.Replace, ImportWizard.ActionFor(chosen, "c1"));
        Assert.Equal(CollisionAction.SkipIncoming, ImportWizard.ActionFor(chosen, "c2"));
        Assert.Equal(["c1", "c3"], chosen.Plan!.Preview!.New.Select(r => r.SourceKey).ToList());
        Assert.Equal(["c1"], chosen.Plan.Preview.ReplaceSourceKeys);
    }

    [Fact]
    public void Collision_choice_clears_the_dry_run_because_the_mapping_changed()
    {
        var state = AtCollisionPreview(out _);
        var dryRun = ImportWizard.StartDryRun(state);
        dryRun = ImportWizard.CompleteDryRun(dryRun, new ImportDryRunReport(true, dryRun.Plan!.MappingHash, 3, 0));
        Assert.True(dryRun.CanCommit);

        // A collision choice is only offered in the preview, so the dry-run step refuses it.
        var refused = ImportWizard.ChooseCollisionAction(dryRun, "c1", CollisionAction.Replace);
        Assert.Equal(WizardStep.DryRun, refused.Step);
        Assert.NotNull(refused.Notice);
        Assert.True(refused.CanCommit);

        var preview = ImportWizard.StartDryRun(state);
        Assert.Null(preview.DryRun);
    }

    [Fact]
    public void Collision_choice_refuses_a_record_that_is_not_in_a_collision()
    {
        var state = AtCollisionPreview(out _);

        var refused = ImportWizard.ChooseCollisionAction(state, "c3", CollisionAction.SkipIncoming);

        Assert.Empty(refused.CollisionChoices);
        Assert.Equal("that record is not part of a collision", refused.Notice);
        Assert.Equal(state.Plan!.MappingHash, refused.Plan!.MappingHash);
    }

    [Fact]
    public void Retry_after_a_failed_commit_returns_to_the_preview_with_the_plan_and_choices()
    {
        var state = AtCollisionPreview(out var wizard);
        state = ImportWizard.ChooseCollisionAction(WithStored(state), "c1", CollisionAction.Replace);
        var dryRun = ImportWizard.CompleteDryRun(ImportWizard.StartDryRun(state), new ImportDryRunReport(true, state.Plan!.MappingHash, 2, 0));
        var commit = ImportWizard.StartCommit(dryRun);
        var failed = ImportWizard.CompleteCommit(commit, new ImportCommitReport(false, 0, "disk full"));
        Assert.Equal(WizardStep.Failed, failed.Step);

        var retried = wizard.Retry(failed);

        Assert.Equal(WizardStep.Preview, retried.Step);
        Assert.Null(retried.Message);
        Assert.Null(retried.Commit);
        Assert.Null(retried.DryRun);
        Assert.Single(retried.CollisionChoices);
        Assert.NotNull(retried.Plan);
    }

    [Fact]
    public void Retry_after_a_failed_detection_runs_the_source_again()
    {
        var calls = 0;
        var wizard = Wizard(new FakeImporter("bazaar.import.a", 0.9, () =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("boom");
            return [Record("k1")];
        }));
        var failed = wizard.ChooseSource(WizardState.Initial, Src(), _noKeys);
        Assert.Equal(WizardStep.Failed, failed.Step);

        var retried = wizard.Retry(failed);

        Assert.Equal(WizardStep.Preview, retried.Step);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Retry_is_refused_outside_the_failed_step()
    {
        var state = AtCollisionPreview(out var wizard);

        var refused = wizard.Retry(state);

        Assert.Equal(WizardStep.Preview, refused.Step);
        Assert.Equal("retry is allowed only after a failure", refused.Notice);
    }

    [Fact]
    public void Existing_objects_are_listed_as_collisions_that_can_be_replaced()
    {
        var state = AtCollisionPreview(out _);
        var existing = new[] { new ExistingObject("card", "Same title", "obj-1", "rev-7") };

        var checkedState = ImportWizard.WithExistingObjects(state, existing);

        Assert.Equal(2, checkedState.Preview!.ExistingCollisions.Count);
        Assert.Equal("obj-1", checkedState.Preview.ExistingCollisions[0].ExistingObjectId);
        var replaced = ImportWizard.ChooseCollisionAction(checkedState, "c1", CollisionAction.Replace);
        Assert.Null(replaced.Notice);
        Assert.Equal(["c1"], replaced.Plan!.Preview!.ReplaceSourceKeys);
        Assert.Equal(["c1", "c2", "c3"], replaced.Plan.Preview.New.Select(r => r.SourceKey).ToList());
    }

    [Fact]
    public void Replace_is_refused_for_a_record_with_no_stored_object()
    {
        var state = AtCollisionPreview(out _);

        var refused = ImportWizard.ChooseCollisionAction(state, "c1", CollisionAction.Replace);

        Assert.Empty(refused.CollisionChoices);
        Assert.Equal("replace needs an object already stored with the same title", refused.Notice);
    }

    [Fact]
    public void A_replace_whose_stored_object_has_gone_is_dropped_when_the_lookup_is_repeated()
    {
        var state = AtCollisionPreview(out _);
        var existing = new[] { new ExistingObject("card", "Same title", "obj-1", "rev-7") };
        var replaced = ImportWizard.ChooseCollisionAction(ImportWizard.WithExistingObjects(state, existing), "c1", CollisionAction.Replace);

        var rechecked = ImportWizard.WithExistingObjects(replaced, []);

        Assert.Empty(rechecked.CollisionChoices);
        Assert.Empty(rechecked.Plan!.Preview!.ReplaceSourceKeys);
        Assert.Empty(rechecked.Preview!.ExistingCollisions);
    }

    [Fact]
    public void Existing_object_lookup_is_refused_outside_the_preview()
    {
        var state = WizardState.Initial;

        var refused = ImportWizard.WithExistingObjects(state, []);

        Assert.Equal("existing objects can be checked only in the preview", refused.Notice);
    }
}
