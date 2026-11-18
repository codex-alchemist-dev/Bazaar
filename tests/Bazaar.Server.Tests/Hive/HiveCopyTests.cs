using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Hive;

/// <summary>Vault stand-in: records commits and answers reads from what was written.</summary>
internal sealed class FakeHiveVault : IMemoryBatchSink, IHiveNoteReader
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public List<MemoryBatch> Commits { get; } = [];

    public bool Fail { get; set; }

    public IReadOnlyDictionary<string, string> Files => _files;

    public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
    {
        if (Fail)
        {
            return Task.FromResult(MemoryCommitResult.Failure("disk full"));
        }

        Commits.Add(batch);
        foreach (var item in batch.Items)
        {
            _files[item.Path] = item.Content;
        }

        return Task.FromResult(MemoryCommitResult.Success("sha-" + Commits.Count));
    }

    public Task<string?> ReadAsync(string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(path, out var text) ? text : null);
}

public class HiveCopyTests
{
    private const string _origin = "project-atlas";
    private const string _target = "project-borealis";
    private const string _notePath = "projects/project-atlas/memory/lessons/retry.md";
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public Rig(int reviewDays = 30, bool grant = true, HiveKind kind = HiveKind.Procedure, IReadOnlySet<HiveKind>? allowed = null)
        {
            Options = new HiveCopyOptions { ReviewDays = reviewDays };
            if (allowed is not null)
            {
                Options = Options with { AllowedKinds = allowed };
            }

            Time = new FakeTimeProvider(_start);
            Grants = new InMemoryHiveGrantStore();
            if (grant)
            {
                Grants.AddAsync(new HiveGrant(_origin, _target, kind, "owner"), CancellationToken.None).GetAwaiter().GetResult();
            }

            Ledger = new InMemoryHiveCopyLedger();
            Vault = new FakeHiveVault();
            Service = new HiveCopyService(new HiveGrantPolicy(Grants, Options), Grants, Ledger, Vault, Vault, Options, Time);
        }

        public HiveCopyOptions Options { get; }

        public FakeTimeProvider Time { get; }

        public InMemoryHiveGrantStore Grants { get; }

        public InMemoryHiveCopyLedger Ledger { get; }

        public FakeHiveVault Vault { get; }

        public HiveCopyService Service { get; }
    }

    private static string Note(
        MemoryKind kind = MemoryKind.Procedural,
        string title = "Retry flaky network calls",
        string body = "Retry with exponential backoff and jitter.\n",
        MemoryTier tier = MemoryTier.AgentVerified,
        bool tainted = false,
        ContentTrust source = ContentTrust.Agent,
        IReadOnlyList<string>? subjects = null) =>
        NoteDocument.Create(
            new MemoryNote("01HZZZZZZZZZZZZZZZZZZZZZZZ", MemoryNoteTypes.TypeFor(kind), title, _origin, kind,
                MemoryNoteStatus.Active, 1, tier, source, tainted, null, [], subjects ?? [], null, []),
            body).Serialize();

    private static HiveCopyRequest Request(
        string text,
        HiveKind kind = HiveKind.Procedure,
        HiveSourceClass cls = HiveSourceClass.Lesson,
        HiveSanitizerTerms? terms = null,
        string note = _notePath) =>
        new(new LessonRef(_origin, note, kind), _target, text, cls, terms ?? HiveSanitizerTerms.ForOrigin(_origin));

    [Fact]
    public async Task No_grant_default_blocks_everything()
    {
        var rig = new Rig(grant: false);
        var outcome = await rig.Service.SubmitAsync(Request(Note()), Ct);

        Assert.Equal(HiveRefusal.NoGrant, outcome.Refusal);
        Assert.Empty(await rig.Ledger.ListPendingAsync(Ct));
        Assert.Empty(rig.Vault.Commits);
    }

    [Fact]
    public async Task Grant_for_another_kind_or_pair_does_not_cover()
    {
        var rig = new Rig(kind: HiveKind.Failure);
        var outcome = await rig.Service.SubmitAsync(Request(Note()), Ct);
        Assert.Equal(HiveRefusal.NoGrant, outcome.Refusal);
    }

    [Fact]
    public async Task Kind_outside_allowed_kinds_is_refused_even_with_grant()
    {
        var rig = new Rig(kind: HiveKind.TechnicalFact);
        var outcome = await rig.Service.SubmitAsync(Request(Note(MemoryKind.Semantic), HiveKind.TechnicalFact), Ct);
        Assert.Equal(HiveRefusal.KindNotAllowed, outcome.Refusal);
    }

    [Fact]
    public async Task Technical_fact_copies_when_operator_allows_it()
    {
        var rig = new Rig(0, kind: HiveKind.TechnicalFact, allowed: new HashSet<HiveKind> { HiveKind.TechnicalFact });
        var outcome = await rig.Service.SubmitAsync(Request(Note(MemoryKind.Semantic, "Postgres NOTIFY payload limit", "The limit is 8000 bytes.\n"), HiveKind.TechnicalFact), Ct);
        Assert.Equal(HiveCopyStatus.Held, outcome.Status);
        Assert.Equal("hive/technical-fact/postgres-notify-payload-limit.md", outcome.Path);
    }

    [Theory]
    [InlineData(MemoryTier.Quarantined)]
    [InlineData(MemoryTier.UntrustedDerived)]
    public async Task T0_and_T1_are_refused(MemoryTier tier)
    {
        var rig = new Rig();
        var outcome = await rig.Service.SubmitAsync(Request(Note(tier: tier)), Ct);
        Assert.Equal(HiveRefusal.TierTooLow, outcome.Refusal);
    }

    [Theory]
    [InlineData(HiveSourceClass.Preference)]
    [InlineData(HiveSourceClass.PrivateMemoryOnly)]
    [InlineData(HiveSourceClass.GcText)]
    [InlineData(HiveSourceClass.VaultText)]
    [InlineData(HiveSourceClass.Reputation)]
    public async Task Excluded_source_classes_are_refused(HiveSourceClass cls)
    {
        var rig = new Rig();
        var outcome = await rig.Service.SubmitAsync(Request(Note(), cls: cls), Ct);
        Assert.Equal(HiveRefusal.ClassNotShareable, outcome.Refusal);
        Assert.Equal(cls.ToString(), outcome.Detail);
    }

    [Theory]
    [InlineData(MemoryKind.Preference)]
    [InlineData(MemoryKind.Episodic)]
    [InlineData(MemoryKind.Working)]
    public async Task Preference_and_non_lesson_note_kinds_are_refused(MemoryKind kind)
    {
        var rig = new Rig();
        var outcome = await rig.Service.SubmitAsync(Request(Note(kind)), Ct);
        Assert.Equal(HiveRefusal.ClassNotShareable, outcome.Refusal);
    }

    [Fact]
    public async Task Subject_tagged_note_is_refused()
    {
        var rig = new Rig();
        var outcome = await rig.Service.SubmitAsync(Request(Note(subjects: ["person-1"])), Ct);
        Assert.Equal(HiveRefusal.SubjectTagged, outcome.Refusal);
    }

    [Fact]
    public async Task Tainted_or_external_source_is_refused()
    {
        var rig = new Rig();
        Assert.Equal(HiveRefusal.TaintedSource, (await rig.Service.SubmitAsync(Request(Note(tainted: true)), Ct)).Refusal);
        Assert.Equal(HiveRefusal.TaintedSource, (await rig.Service.SubmitAsync(Request(Note(source: ContentTrust.External)), Ct)).Refusal);
    }

    [Fact]
    public async Task Kind_mismatch_and_unparseable_are_refused()
    {
        var rig = new Rig();
        Assert.Equal(HiveRefusal.KindMismatch, (await rig.Service.SubmitAsync(Request(Note(MemoryKind.Failure)), Ct)).Refusal);
        Assert.Equal(HiveRefusal.Unparseable, (await rig.Service.SubmitAsync(Request("no front matter"), Ct)).Refusal);
    }

    [Fact]
    public async Task Deprecated_source_is_refused()
    {
        var rig = new Rig();
        var doc = NoteDocument.Parse(Note()).Document!;
        var dead = doc.WithNote(doc.Note with { Status = MemoryNoteStatus.Deprecated, Deprecated = new MemoryDeprecation("old", "owner", "2026-01-01") });
        Assert.Equal(HiveRefusal.NotActive, (await rig.Service.SubmitAsync(Request(dead.Serialize()), Ct)).Refusal);
    }

    [Fact]
    public async Task Sanitizer_rejection_blocks_the_copy_and_never_echoes_the_match()
    {
        var rig = new Rig();
        var outcome = await rig.Service.SubmitAsync(Request(Note(body: "In project-atlas we retried.\n")), Ct);
        Assert.Equal(HiveRefusal.SanitizerRejected, outcome.Refusal);
        Assert.Equal(nameof(HiveSanitizerFinding.OriginProject), outcome.Detail);
        Assert.Empty(await rig.Ledger.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task Duplicate_submission_is_refused()
    {
        var rig = new Rig();
        Assert.Equal(HiveCopyStatus.Held, (await rig.Service.SubmitAsync(Request(Note()), Ct)).Status);
        Assert.Equal(HiveRefusal.Duplicate, (await rig.Service.SubmitAsync(Request(Note()), Ct)).Refusal);
    }

    [Fact]
    public async Task Review_window_30_holds_then_releases_after_30_days()
    {
        var rig = new Rig(30);
        var held = await rig.Service.SubmitAsync(Request(Note()), Ct);
        Assert.Equal(HiveCopyStatus.Held, held.Status);
        Assert.Equal(_start.AddDays(30), held.ReleaseAt);

        rig.Time.Advance(TimeSpan.FromDays(29));
        Assert.Empty(await rig.Service.ReleaseDueAsync(Ct));
        Assert.Empty(rig.Vault.Commits);

        rig.Time.Advance(TimeSpan.FromDays(1));
        var released = await rig.Service.ReleaseDueAsync(Ct);
        Assert.Equal(HiveCopyStatus.Copied, Assert.Single(released).Status);
        Assert.Single(rig.Vault.Commits);
        Assert.Equal("hive", rig.Vault.Commits[0].AgentId);
        Assert.Empty(await rig.Ledger.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task Review_window_0_keeps_every_batch_held_until_owner_approves()
    {
        var rig = new Rig(0);
        var held = await rig.Service.SubmitAsync(Request(Note()), Ct);
        Assert.Null(held.ReleaseAt);

        rig.Time.Advance(TimeSpan.FromDays(400));
        Assert.Empty(await rig.Service.ReleaseDueAsync(Ct));
        Assert.Empty(rig.Vault.Commits);

        var approved = await rig.Service.ApproveAsync(held.PendingId!.Value, Ct);
        Assert.Equal(HiveCopyStatus.Copied, approved.Status);
        Assert.Single(rig.Vault.Commits);
    }

    [Fact]
    public async Task Owner_can_reject_a_held_copy()
    {
        var rig = new Rig();
        var held = await rig.Service.SubmitAsync(Request(Note()), Ct);
        Assert.True(await rig.Service.RejectAsync(held.PendingId!.Value, Ct));
        Assert.Equal(HiveRefusal.NotFound, (await rig.Service.ApproveAsync(held.PendingId.Value, Ct)).Refusal);
    }

    [Fact]
    public async Task Grant_revoked_during_the_window_drops_the_copy_at_release()
    {
        var rig = new Rig(1);
        await rig.Service.SubmitAsync(Request(Note()), Ct);
        await rig.Grants.RemoveAsync(_origin, _target, HiveKind.Procedure, Ct);
        rig.Time.Advance(TimeSpan.FromDays(2));

        var outcome = Assert.Single(await rig.Service.ReleaseDueAsync(Ct));
        Assert.Equal(HiveRefusal.NoGrant, outcome.Refusal);
        Assert.Empty(rig.Vault.Commits);
    }

    [Fact]
    public async Task Copy_carries_front_matter_and_never_promotes_itself()
    {
        var rig = new Rig(0);
        var held = await rig.Service.SubmitAsync(Request(Note(tier: MemoryTier.OwnerPromoted)), Ct);
        await rig.Service.ApproveAsync(held.PendingId!.Value, Ct);

        var text = rig.Vault.Files["hive/procedure/retry-flaky-network-calls.md"];
        var doc = NoteDocument.Parse(text).Document!;
        Assert.Equal(MemoryKind.Hive, doc.Note.Kind);
        Assert.Equal(MemoryTier.AgentVerified, doc.Note.Trust);
        Assert.Null(doc.Note.Project);
        Assert.Contains("origin_project: project-atlas\n", text);
        Assert.Contains("origin_note: " + _notePath + "\n", text);
        Assert.Contains("origin_kind: procedure\n", text);
        Assert.Contains("trust: T3\n", text);
        Assert.True(MemoryAreas.CheckPath("hive/procedure/retry-flaky-network-calls.md", MemoryKind.Hive).IsValid);
    }

    [Fact]
    public async Task Failed_commit_keeps_the_copy_held()
    {
        var rig = new Rig(0);
        var held = await rig.Service.SubmitAsync(Request(Note()), Ct);
        rig.Vault.Fail = true;

        var outcome = await rig.Service.ApproveAsync(held.PendingId!.Value, Ct);
        Assert.Equal(HiveRefusal.CommitFailed, outcome.Refusal);
        Assert.Single(await rig.Ledger.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task Different_lessons_with_one_title_get_distinct_paths()
    {
        var rig = new Rig(0);
        var a = await rig.Service.SubmitAsync(Request(Note(), note: "projects/project-atlas/memory/lessons/a.md"), Ct);
        var b = await rig.Service.SubmitAsync(Request(Note(), note: "projects/project-atlas/memory/lessons/b.md"), Ct);
        await rig.Service.ApproveAsync(a.PendingId!.Value, Ct);
        var second = await rig.Service.ApproveAsync(b.PendingId!.Value, Ct);

        Assert.NotEqual("hive/procedure/retry-flaky-network-calls.md", second.Path);
        Assert.Equal(2, rig.Vault.Files.Count);
    }

    [Fact]
    public async Task Revoke_deprecates_copies_and_keeps_the_files()
    {
        var rig = new Rig(0);
        var held = await rig.Service.SubmitAsync(Request(Note()), Ct);
        var copied = await rig.Service.ApproveAsync(held.PendingId!.Value, Ct);
        var bodyBefore = NoteDocument.Parse(rig.Vault.Files[copied.Path!]).Document!.Body;

        var result = await rig.Service.RevokeAsync(_origin, _target, HiveKind.Procedure, "owner", Ct);

        Assert.Equal([copied.Path!], result.DeprecatedPaths);
        Assert.True(rig.Vault.Files.ContainsKey(copied.Path!));
        var doc = NoteDocument.Parse(rig.Vault.Files[copied.Path!]).Document!;
        Assert.Equal(MemoryNoteStatus.Deprecated, doc.Note.Status);
        Assert.Equal(2, doc.Note.Version);
        Assert.Equal("owner", doc.Note.Deprecated!.By);
        Assert.Equal(bodyBefore, doc.Body);
        Assert.Contains("origin_project: project-atlas\n", rig.Vault.Files[copied.Path!]);
        Assert.False(await rig.Grants.IsGrantedAsync(_origin, _target, HiveKind.Procedure, Ct));
        Assert.All(await rig.Ledger.ListCopiesAsync(Ct), c => Assert.True(c.Revoked));
    }

    [Fact]
    public async Task Revoke_drops_held_copies_under_the_grant()
    {
        var rig = new Rig();
        await rig.Service.SubmitAsync(Request(Note()), Ct);
        await rig.Service.RevokeAsync(_origin, _target, HiveKind.Procedure, "owner", Ct);
        Assert.Empty(await rig.Ledger.ListPendingAsync(Ct));
        Assert.Empty(rig.Vault.Commits);
    }

    [Fact]
    public async Task Revoke_keeps_a_copy_active_while_another_target_is_still_granted()
    {
        var rig = new Rig(0);
        await rig.Grants.AddAsync(new HiveGrant(_origin, "project-cygnus", HiveKind.Procedure, "owner"), Ct);
        var one = await rig.Service.SubmitAsync(Request(Note()), Ct);
        var two = await rig.Service.SubmitAsync(Request(Note()) with { TargetProject = "project-cygnus" }, Ct);
        await rig.Service.ApproveAsync(one.PendingId!.Value, Ct);
        await rig.Service.ApproveAsync(two.PendingId!.Value, Ct);

        var result = await rig.Service.RevokeAsync(_origin, _target, HiveKind.Procedure, "owner", Ct);

        Assert.Empty(result.DeprecatedPaths);
        Assert.Single(result.KeptPaths);
        Assert.Equal(MemoryNoteStatus.Active, NoteDocument.Parse(rig.Vault.Files[result.KeptPaths[0]]).Document!.Note.Status);
    }

    [Fact]
    public async Task Failed_revoke_commit_is_reported_and_can_be_retried()
    {
        var rig = new Rig(0);
        var held = await rig.Service.SubmitAsync(Request(Note()), Ct);
        await rig.Service.ApproveAsync(held.PendingId!.Value, Ct);
        rig.Vault.Fail = true;

        var failed = await rig.Service.RevokeAsync(_origin, _target, HiveKind.Procedure, "owner", Ct);
        Assert.Equal("disk full", failed.CommitFailure);

        rig.Vault.Fail = false;
        var retry = await rig.Service.RevokeAsync(_origin, _target, HiveKind.Procedure, "owner", Ct);
        Assert.Single(retry.DeprecatedPaths);
    }

    [Fact]
    public void Options_reject_review_days_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HiveCopyOptions { ReviewDays = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HiveCopyOptions { ReviewDays = 366 });
    }
}
