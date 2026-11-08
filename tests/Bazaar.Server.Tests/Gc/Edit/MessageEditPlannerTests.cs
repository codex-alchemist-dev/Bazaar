using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Edit;

public class MessageEditPlannerTests
{
    private static Guid GcId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static Guid MessageId => Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static Guid AuthorId => Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static Guid OtherId => Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static DateTimeOffset CreatedAt => new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static GcRecord LiveGc() => new(GcId, null, "general", null);

    /// <summary>Plans an edit with defaults that succeed; each test overrides only what it checks.</summary>
    private static MessageEditResult PlanEdit(
        GcRecord? gc = null,
        GcViewer? editor = null,
        Guid? originalAuthorId = null,
        string newBody = "edited text",
        string currentBody = "original text",
        int currentVersion = 1,
        long currentVersionSeq = 5,
        DateTimeOffset? now = null,
        TimeSpan? editWindow = null,
        int maxBodyBytes = MessageEditPlanner.DefaultMaxBodyBytes,
        SourceTrust trust = SourceTrust.MemberHuman,
        bool messageWasActedOn = false)
    {
        return MessageEditPlanner.Plan(
            gc ?? LiveGc(),
            editor ?? new GcViewer(AuthorId, PrincipalKind.Human),
            MessageId,
            originalAuthorId ?? AuthorId,
            currentVersion,
            currentVersionSeq,
            currentBody,
            CreatedAt,
            newBody,
            now ?? CreatedAt.AddMinutes(5),
            editWindow,
            maxBodyBytes,
            trust,
            messageWasActedOn);
    }

    private static MessageEditPlan Accepted(MessageEditResult result)
    {
        var accepted = Assert.IsType<MessageEditAccepted>(result);
        return accepted.Plan;
    }

    private static MessageEditRefusal Refused(MessageEditResult result)
    {
        return Assert.IsType<MessageEditRefused>(result).Reason;
    }

    [Fact]
    public void Successful_edit_adds_version_two_with_placeholder_seq()
    {
        var plan = Accepted(PlanEdit());

        Assert.Equal(GcId, plan.NewVersion.GcId);
        Assert.Equal(MessageId, plan.NewVersion.MessageId);
        Assert.Equal(2, plan.NewVersion.Version);
        Assert.Equal("edited text", plan.NewVersion.Body);
        Assert.Equal(AuthorId, plan.NewVersion.EditorId);
        Assert.Equal(0L, plan.NewVersion.Seq);
        Assert.Equal(CreatedAt.AddMinutes(5), plan.NewVersion.CreatedAt);
    }

    [Fact]
    public void Successful_edit_emits_message_edit_event_with_message_and_version()
    {
        var plan = Accepted(PlanEdit(trust: SourceTrust.MemberHuman));

        var edit = plan.EditEvent;
        Assert.Equal(GcId, edit.GcId);
        Assert.Equal(GcEventKinds.MessageEdit, edit.Kind);
        Assert.Equal(AuthorKind.Human, edit.AuthorKind);
        Assert.Equal(AuthorId, edit.AuthorId);
        Assert.Equal(SourceTrust.MemberHuman, edit.SourceTrust);
        Assert.Equal(MessageId, edit.MessageId);
        Assert.Equal(2, edit.Version);
        Assert.Null(edit.TargetSeq);
        Assert.Null(edit.PayloadJson);
    }

    [Fact]
    public void Edit_carries_the_editors_own_trust_not_a_copy_of_the_original()
    {
        var plan = Accepted(PlanEdit(trust: SourceTrust.Agent));

        Assert.Equal(SourceTrust.Agent, plan.EditEvent.SourceTrust);
    }

    [Fact]
    public void Version_increments_from_the_current_version()
    {
        var plan = Accepted(PlanEdit(currentVersion: 3, currentBody: "third"));

        Assert.Equal(4, plan.NewVersion.Version);
        Assert.Equal(4, plan.EditEvent.Version);
    }

    [Theory]
    [InlineData(PrincipalKind.Owner, AuthorKind.Owner)]
    [InlineData(PrincipalKind.Human, AuthorKind.Human)]
    [InlineData(PrincipalKind.Agent, AuthorKind.Agent)]
    public void Author_kind_maps_from_the_viewer_kind(PrincipalKind kind, AuthorKind expected)
    {
        var plan = Accepted(PlanEdit(editor: new GcViewer(AuthorId, kind)));

        Assert.Equal(expected, plan.EditEvent.AuthorKind);
    }

    [Fact]
    public void Non_author_is_refused_even_when_the_owner_asks()
    {
        var result = PlanEdit(editor: new GcViewer(OtherId, PrincipalKind.Owner));

        Assert.Equal(MessageEditRefusal.NotAuthor, Refused(result));
    }

    [Fact]
    public void Author_check_comes_before_archive_check()
    {
        var archived = new GcRecord(GcId, null, "general", CreatedAt);

        var result = PlanEdit(gc: archived, editor: new GcViewer(OtherId, PrincipalKind.Human));

        Assert.Equal(MessageEditRefusal.NotAuthor, Refused(result));
    }

    [Fact]
    public void Archived_gc_is_read_only_and_refuses_the_edit()
    {
        var archived = new GcRecord(GcId, null, "general", CreatedAt.AddDays(1));

        var result = PlanEdit(gc: archived);

        Assert.Equal(MessageEditRefusal.ArchivedGc, Refused(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Empty_or_whitespace_body_is_refused(string body)
    {
        var result = PlanEdit(newBody: body);

        Assert.Equal(MessageEditRefusal.EmptyBody, Refused(result));
    }

    [Fact]
    public void Body_cap_counts_utf8_bytes_not_characters()
    {
        // Each "é" is one character but two UTF-8 bytes. Five of them are 10 bytes: exactly the cap.
        var atCap = Accepted(PlanEdit(newBody: new string('é', 5), maxBodyBytes: 10));
        Assert.Equal(new string('é', 5), atCap.NewVersion.Body);

        // Six are 12 bytes: over the cap, although only 6 characters, which is under 10.
        var result = PlanEdit(newBody: new string('é', 6), maxBodyBytes: 10);

        Assert.Equal(MessageEditRefusal.BodyTooLarge, Refused(result));
    }

    [Fact]
    public void Default_body_cap_is_32768_bytes()
    {
        Assert.Equal(32768, MessageEditPlanner.DefaultMaxBodyBytes);

        Accepted(PlanEdit(newBody: new string('a', 32768)));

        Assert.Equal(
            MessageEditRefusal.BodyTooLarge,
            Refused(PlanEdit(newBody: new string('a', 32769))));
    }

    [Fact]
    public void Edit_exactly_at_the_window_end_is_allowed()
    {
        var window = TimeSpan.FromHours(1);

        Accepted(PlanEdit(editWindow: window, now: CreatedAt + window));
    }

    [Fact]
    public void Edit_one_tick_after_the_window_end_is_refused()
    {
        var window = TimeSpan.FromHours(1);

        var result = PlanEdit(editWindow: window, now: CreatedAt + window + TimeSpan.FromTicks(1));

        Assert.Equal(MessageEditRefusal.OutsideEditWindow, Refused(result));
    }

    [Fact]
    public void Null_edit_window_is_unlimited()
    {
        Accepted(PlanEdit(editWindow: null, now: CreatedAt.AddYears(10)));
    }

    [Fact]
    public void Identical_body_is_refused_as_unchanged()
    {
        var result = PlanEdit(newBody: "same text", currentBody: "same text");

        Assert.Equal(MessageEditRefusal.UnchangedBody, Refused(result));
    }

    [Fact]
    public void Body_comparison_is_ordinal_so_case_changes_are_edits()
    {
        Accepted(PlanEdit(newBody: "Same Text", currentBody: "same text"));
    }

    [Fact]
    public void No_system_note_when_software_has_not_acted_on_the_message()
    {
        var plan = Accepted(PlanEdit(messageWasActedOn: false));

        Assert.Null(plan.SystemNote);
    }

    [Fact]
    public void Acted_on_message_adds_source_edited_after_use_note()
    {
        var plan = Accepted(PlanEdit(messageWasActedOn: true, currentVersionSeq: 42));

        var note = Assert.IsType<GcEventDraft>(plan.SystemNote);
        Assert.Equal(GcId, note.GcId);
        Assert.Equal(GcEventKinds.System, note.Kind);
        Assert.Equal(AuthorKind.System, note.AuthorKind);
        Assert.Equal(MessageEditPlanner.SystemAuthorId, note.AuthorId);
        Assert.Equal(SourceTrust.System, note.SourceTrust);
        Assert.Equal(MessageId, note.MessageId);
        Assert.Null(note.Version);
        Assert.Equal(42L, note.TargetSeq);
        Assert.Equal(MessageEditPlanner.SystemNotePayloadJson, note.PayloadJson);
        Assert.Contains("source edited after use", note.PayloadJson);
    }

    [Fact]
    public void Acted_on_note_does_not_replace_the_message_edit_event()
    {
        var plan = Accepted(PlanEdit(messageWasActedOn: true));

        Assert.Equal(GcEventKinds.MessageEdit, plan.EditEvent.Kind);
        Assert.NotEqual(GcEventKinds.System, plan.EditEvent.Kind);
    }

    [Fact]
    public void Non_positive_current_version_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanEdit(currentVersion: 0));
    }
}
