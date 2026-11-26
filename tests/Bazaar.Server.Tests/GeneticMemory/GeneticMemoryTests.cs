using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Memory;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;

namespace Bazaar.Server.Tests.GeneticMemory;

public class GeneticMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeVault : IMemoryBatchSink
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public bool Fail { get; set; }

        public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
        {
            if (Fail)
            {
                return Task.FromResult(MemoryCommitResult.Failure("disk full"));
            }

            foreach (var item in batch.Items)
            {
                Files[item.Path] = item.Content;
            }

            return Task.FromResult(MemoryCommitResult.Success("sha"));
        }
    }

    private static (GeneticMemoryService Service, FakeVault Vault) Rig(GmtReviewer reviewer = GmtReviewer.Architect)
    {
        var vault = new FakeVault();
        return (new GeneticMemoryService(new InMemoryGmtLedger(), new GmtLessonWriter(vault), new GmtSecretScanner(), new GmtOptions { LessonReviewer = reviewer }), vault);
    }

    private static GmtStageRequest Lesson(
        string title = "Retry with backoff",
        string body = "Retry flaky network calls with exponential backoff and a cap.",
        GmtContentClass contentClass = GmtContentClass.Lesson,
        bool tainted = false,
        bool quarantined = false,
        GmtTerms? terms = null,
        GmtLessonKind kind = GmtLessonKind.Procedure,
        string child = "child-1",
        string target = "agent-new") =>
        new(child, target, contentClass, kind, title, body, "trace/abc", tainted, quarantined, terms ?? GmtTerms.None);

    [Fact]
    public async Task A_staged_candidate_is_not_in_the_vault_until_accepted()
    {
        var (service, vault) = Rig();

        var staged = await service.StageAsync(Lesson(), Ct);

        Assert.True(staged.Succeeded);
        Assert.Empty(vault.Files);
        Assert.Single(await service.QueueAsync(GmtReviewer.Architect, Ct));
    }

    [Fact]
    public async Task Accepting_writes_a_private_lesson_note_for_the_receiver_at_t2_with_provenance()
    {
        var (service, vault) = Rig();
        var staged = await service.StageAsync(Lesson(), Ct);

        var accepted = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Architect, null, null, Ct);

        Assert.True(accepted.Succeeded);
        Assert.Equal("agents/agent-new/memory/lessons/retry-with-backoff.md", accepted.Path);
        var note = NoteDocument.Parse(vault.Files[accepted.Path!]).Document!.Note;
        Assert.Equal(MemoryKind.Procedural, note.Kind);
        Assert.Equal(MemoryTier.AgentVerified, note.Trust);
        Assert.False(note.Tainted);
        Assert.Contains("origin_agent: child-1", vault.Files[accepted.Path!], StringComparison.Ordinal);
        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task A_tainted_childs_lesson_stays_t1_after_acceptance()
    {
        var (service, vault) = Rig();
        var staged = await service.StageAsync(Lesson(tainted: true), Ct);

        var accepted = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, null, null, Ct);

        var note = NoteDocument.Parse(vault.Files[accepted.Path!]).Document!.Note;
        Assert.Equal(MemoryTier.UntrustedDerived, note.Trust);
        Assert.True(note.Tainted);
    }

    [Fact]
    public async Task A_failure_lesson_goes_under_failures()
    {
        var (service, _) = Rig();
        var staged = await service.StageAsync(Lesson(kind: GmtLessonKind.Failure), Ct);

        var accepted = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, null, null, Ct);

        Assert.Equal("agents/agent-new/memory/failures/retry-with-backoff.md", accepted.Path);
    }

    [Theory]
    [InlineData(GmtContentClass.Preference)]
    [InlineData(GmtContentClass.PrivateMemory)]
    [InlineData(GmtContentClass.GcText)]
    [InlineData(GmtContentClass.Reputation)]
    [InlineData(GmtContentClass.SoulExport)]
    public async Task Private_memory_preferences_gc_text_reputation_and_soul_export_never_stage(GmtContentClass contentClass)
    {
        var (service, vault) = Rig();

        var outcome = await service.StageAsync(Lesson(contentClass: contentClass), Ct);

        Assert.Equal(GmtRefusal.NotTransferable, outcome.Refusal);
        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
        Assert.Empty(vault.Files);
    }

    [Fact]
    public async Task A_quarantined_child_stages_nothing()
    {
        var (service, _) = Rig();

        var outcome = await service.StageAsync(Lesson(quarantined: true), Ct);

        Assert.Equal(GmtRefusal.ChildQuarantined, outcome.Refusal);
    }

    [Fact]
    public async Task A_secret_or_a_named_person_customer_or_project_is_refused_without_echoing_the_text()
    {
        var (service, _) = Rig();
        var terms = new GmtTerms(["Dana Reyes"], ["Acme Corp"], ["project-atlas"]);

        var person = await service.StageAsync(Lesson(body: "Ask dana reyes first.", terms: terms), Ct);
        var customer = await service.StageAsync(Lesson(body: "Acme Corp wants it.", terms: terms), Ct);
        var project = await service.StageAsync(Lesson(body: "Seen in Project_Atlas.", terms: terms), Ct);
        var secret = await service.StageAsync(Lesson(body: "key ghp_abcdefghijklmnopqrstuvwxyz0123456789"), Ct);

        Assert.Equal(GmtRefusal.PersonalData, person.Refusal);
        Assert.Equal("Person", person.Detail);
        Assert.Equal(GmtRefusal.PersonalData, customer.Refusal);
        Assert.Equal(GmtRefusal.PersonalData, project.Refusal);
        Assert.Equal(GmtRefusal.SecretFound, secret.Refusal);
        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task A_short_name_inside_another_word_is_not_a_hit()
    {
        var (service, _) = Rig();

        var outcome = await service.StageAsync(Lesson(body: "Use a bounded queue.", terms: new GmtTerms(["Qu"], [], [])), Ct);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task The_same_child_staging_the_same_title_twice_is_a_duplicate()
    {
        var (service, _) = Rig();
        await service.StageAsync(Lesson(), Ct);

        var again = await service.StageAsync(Lesson(), Ct);

        Assert.Equal(GmtRefusal.Duplicate, again.Refusal);
    }

    [Fact]
    public async Task A_child_cannot_transfer_to_itself()
    {
        var (service, _) = Rig();

        var outcome = await service.StageAsync(Lesson(child: "a", target: "a"), Ct);

        Assert.Equal(GmtRefusal.SelfTransfer, outcome.Refusal);
    }

    [Fact]
    public async Task Tool_grants_and_charter_text_are_owner_only_even_when_the_architect_reviews_lessons()
    {
        var (service, vault) = Rig(GmtReviewer.Architect);
        var grant = await service.StageAsync(Lesson(title: "Grant shell", contentClass: GmtContentClass.ToolGrant), Ct);
        var charter = await service.StageAsync(Lesson(title: "Charter edit", contentClass: GmtContentClass.CharterText), Ct);

        Assert.Empty(await service.QueueAsync(GmtReviewer.Architect, Ct));
        Assert.Equal(2, (await service.QueueAsync(GmtReviewer.Owner, Ct)).Count);
        var denied = await service.AcceptAsync(grant.Id!.Value, GmtReviewer.Architect, null, null, Ct);
        var allowed = await service.AcceptAsync(charter.Id!.Value, GmtReviewer.Owner, null, null, Ct);

        Assert.Equal(GmtRefusal.ReviewerNotAllowed, denied.Refusal);
        Assert.True(allowed.Succeeded);
        Assert.Null(allowed.Path);
        Assert.Empty(vault.Files);
    }

    [Fact]
    public async Task With_the_owner_as_reviewer_the_architect_cannot_decide_lessons()
    {
        var (service, _) = Rig(GmtReviewer.Owner);
        var staged = await service.StageAsync(Lesson(), Ct);

        Assert.Empty(await service.QueueAsync(GmtReviewer.Architect, Ct));
        var denied = await service.RejectAsync(staged.Id!.Value, GmtReviewer.Architect, Ct);

        Assert.Equal(GmtRefusal.ReviewerNotAllowed, denied.Refusal);
    }

    [Fact]
    public async Task A_rejected_candidate_writes_nothing_and_cannot_be_decided_again()
    {
        var (service, vault) = Rig();
        var staged = await service.StageAsync(Lesson(), Ct);

        var rejected = await service.RejectAsync(staged.Id!.Value, GmtReviewer.Architect, Ct);
        var again = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, null, null, Ct);

        Assert.True(rejected.Succeeded);
        Assert.Equal(GmtRefusal.AlreadyDecided, again.Refusal);
        Assert.Empty(vault.Files);
    }

    [Fact]
    public async Task An_unknown_id_is_not_found()
    {
        var (service, _) = Rig();

        var outcome = await service.AcceptAsync(Guid.NewGuid(), GmtReviewer.Owner, null, null, Ct);

        Assert.Equal(GmtRefusal.NotFound, outcome.Refusal);
    }

    [Fact]
    public async Task An_edit_replaces_the_text_and_a_secret_in_an_edit_is_refused()
    {
        var (service, vault) = Rig();
        var staged = await service.StageAsync(Lesson(), Ct);

        var bad = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, null, "token ghp_abcdefghijklmnopqrstuvwxyz0123456789", Ct);
        var good = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, "Back off on retry", "Cap the delay at 30 seconds.", Ct);

        Assert.Equal(GmtRefusal.SecretFound, bad.Refusal);
        Assert.True(good.Succeeded);
        Assert.Equal("agents/agent-new/memory/lessons/back-off-on-retry.md", good.Path);
        Assert.Contains("Cap the delay", NoteDocument.Parse(vault.Files[good.Path!]).Document!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_commit_leaves_the_candidate_staged()
    {
        var (service, vault) = Rig();
        var staged = await service.StageAsync(Lesson(), Ct);
        vault.Fail = true;

        var failed = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, null, null, Ct);
        vault.Fail = false;
        var retried = await service.AcceptAsync(staged.Id!.Value, GmtReviewer.Owner, null, null, Ct);

        Assert.Equal(GmtRefusal.CommitFailed, failed.Refusal);
        Assert.True(retried.Succeeded);
    }

    [Fact]
    public async Task Two_lessons_with_the_same_title_for_one_receiver_get_distinct_paths()
    {
        var (service, vault) = Rig();
        var a = await service.StageAsync(Lesson(child: "child-1"), Ct);
        var b = await service.StageAsync(Lesson(child: "child-2"), Ct);

        var first = await service.AcceptAsync(a.Id!.Value, GmtReviewer.Owner, null, null, Ct);
        var second = await service.AcceptAsync(b.Id!.Value, GmtReviewer.Owner, null, null, Ct);

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, vault.Files.Count);
    }

    [Fact]
    public void Every_written_path_is_a_valid_private_memory_path()
    {
        Assert.True(MemoryAreas.CheckPath("agents/agent-new/memory/lessons/x.md", MemoryKind.Procedural).IsValid);
    }
}
