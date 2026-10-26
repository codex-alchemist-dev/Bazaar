using Bazaar.Client.Core.Gc;
using Bazaar.Contracts.Gc;

namespace Bazaar.Client.Core.Tests.Gc;

public class GcEntryBuilderTests
{
    private static readonly Guid _gcId = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _alice = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly Guid _bob = Guid.Parse("0196a6f0-0000-7000-8000-000000000003");
    private static readonly Guid _msgOne = Guid.Parse("0196a6f0-0000-7000-8000-000000000010");
    private static readonly Guid _msgTwo = Guid.Parse("0196a6f0-0000-7000-8000-000000000011");

    // A fixed +10:00 zone, so local-time assertions do not depend on the machine's time zone database.
    private static readonly TimeZoneInfo _zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");

    private static readonly DateTimeOffset _t0 = new(2026, 10, 25, 5, 0, 0, TimeSpan.Zero); // 15:00 in +10

    private static readonly GcPrincipal[] _principals =
    [
        new(_alice, PrincipalKind.Owner, "Dom"),
        new(_bob, PrincipalKind.Agent, "Bob"),
    ];

    private static GcEvent Event(
        long seq,
        string kind,
        Guid author,
        DateTimeOffset at,
        Guid? messageId = null,
        int? version = null,
        string? payload = null,
        long? targetSeq = null,
        AuthorKind authorKind = AuthorKind.Human) => new(
        _gcId, seq, Guid.NewGuid(), kind, authorKind, author, SourceTrust.MemberHuman,
        TaintedAtWrite: false, TaintSourcesJson: null, MessageId: messageId, Version: version,
        TargetSeq: targetSeq, ReplyToSeq: null, PayloadJson: payload, CreatedAt: at);

    private static GcEvent Message(long seq, Guid author, DateTimeOffset at, Guid messageId) =>
        Event(seq, GcEventKinds.Message, author, at, messageId, version: 1);

    private static GcMessage Current(Guid messageId, Guid author, DateTimeOffset at, string body, int version = 1) =>
        new(_gcId, messageId, author, FirstSeq: 1, version, body, Edited: version > 1, at);

    private static GcEntryBuilder Builder(TimeSpan? window = null) => new(_zone, window);

    [Fact]
    public void Empty_log_gives_no_entries()
    {
        var entries = Builder().Build([], [], _principals);

        Assert.Empty(entries);
    }

    [Fact]
    public void Entries_come_out_in_seq_order_and_duplicate_seqs_show_once()
    {
        var events = new[]
        {
            Event(3, GcEventKinds.System, _alice, _t0.AddMinutes(2), payload: "three"),
            Event(1, GcEventKinds.System, _alice, _t0, payload: "one"),
            Event(3, GcEventKinds.System, _alice, _t0.AddMinutes(2), payload: "three again"),
        };

        var entries = Builder().Build(events, [], _principals);

        Assert.Equal([1L, 3L], entries.Select(x => x.Header.Seq));
    }

    [Fact]
    public void Events_from_two_gcs_are_rejected()
    {
        var other = Event(2, GcEventKinds.System, _alice, _t0) with { GcId = Guid.NewGuid() };

        Assert.Throws<ArgumentException>(() =>
            Builder().Build([Event(1, GcEventKinds.System, _alice, _t0), other], [], _principals));
    }

    [Fact]
    public void Message_row_shows_current_body_version_and_edited_flag()
    {
        var events = new[] { Message(1, _alice, _t0, _msgOne) };
        var messages = new[] { Current(_msgOne, _alice, _t0, "second take", version: 2) };

        var entry = Assert.IsType<GcMessageEntryViewModel>(Assert.Single(Builder().Build(events, messages, _principals)));

        Assert.Equal("second take", entry.Body);
        Assert.Equal(2, entry.Version);
        Assert.True(entry.IsEdited);
        Assert.Equal(_msgOne, entry.MessageId);
    }

    [Fact]
    public void Edits_fold_into_their_message_and_add_no_row()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Event(2, GcEventKinds.MessageEdit, _alice, _t0.AddMinutes(1), _msgOne, version: 2),
            Message(3, _bob, _t0.AddMinutes(2), _msgTwo),
        };
        var messages = new[]
        {
            Current(_msgOne, _alice, _t0, "fixed", version: 2),
            Current(_msgTwo, _bob, _t0.AddMinutes(2), "hello"),
        };

        var entries = Builder().Build(events, messages, _principals);

        Assert.Equal([1L, 3L], entries.Select(x => x.Header.Seq));
        Assert.Equal("fixed", Assert.IsType<GcMessageEntryViewModel>(entries[0]).Body);
    }

    [Fact]
    public void Lone_edit_event_gives_no_entry()
    {
        var events = new[] { Event(1, GcEventKinds.MessageEdit, _alice, _t0, _msgOne, version: 2) };

        Assert.Empty(Builder().Build(events, [], _principals));
    }

    [Fact]
    public void Message_missing_from_the_store_has_no_body_and_uses_the_event_version()
    {
        var events = new[] { Message(1, _alice, _t0, _msgOne) };

        var entry = Assert.IsType<GcMessageEntryViewModel>(Assert.Single(Builder().Build(events, [], _principals)));

        Assert.Null(entry.Body);
        Assert.Equal(1, entry.Version);
        Assert.False(entry.IsEdited);
    }

    [Fact]
    public void Messages_of_other_gcs_are_ignored()
    {
        var events = new[] { Message(1, _alice, _t0, _msgOne) };
        var stray = Current(_msgOne, _alice, _t0, "not ours") with { GcId = Guid.NewGuid() };

        var entry = Assert.IsType<GcMessageEntryViewModel>(Assert.Single(Builder().Build(events, [stray], _principals)));

        Assert.Null(entry.Body);
    }

    [Fact]
    public void Author_name_and_principal_kind_come_from_the_principal_list()
    {
        var events = new[] { Message(1, _bob, _t0, _msgOne), Message(2, Guid.NewGuid(), _t0.AddHours(1), _msgTwo) };

        var entries = Builder().Build(events, [], _principals);

        Assert.Equal("Bob", entries[0].Header.AuthorName);
        Assert.Equal(PrincipalKind.Agent, entries[0].Header.AuthorPrincipalKind);
        Assert.Null(entries[1].Header.AuthorName);
        Assert.Null(entries[1].Header.AuthorPrincipalKind);
    }

    [Fact]
    public void Non_message_kinds_keep_payload_target_and_reply_fields()
    {
        var events = new[]
        {
            Event(1, GcEventKinds.Quarantine, _alice, _t0, payload: "{\"reason\":\"injection\"}", targetSeq: 7),
            Event(2, GcEventKinds.Structured, _bob, _t0.AddMinutes(1), payload: "{\"task\":\"done\"}", authorKind: AuthorKind.Agent),
        };

        var entries = Builder().Build(events, [], _principals);

        var quarantine = Assert.IsType<GcEventEntryViewModel>(entries[0]);
        Assert.Equal("{\"reason\":\"injection\"}", quarantine.PayloadJson);
        Assert.Equal(7, quarantine.TargetSeq);
        var structured = Assert.IsType<GcEventEntryViewModel>(entries[1]);
        Assert.Equal(AuthorKind.Agent, structured.Header.AuthorKind);
        Assert.Equal(GcEventKinds.Structured, structured.Header.Kind);
    }

    [Fact]
    public void Unknown_kind_becomes_a_placeholder_that_names_the_kind()
    {
        var events = new[] { Event(1, "council.open", _alice, _t0) };

        var entry = Assert.IsType<GcUnknownEntryViewModel>(Assert.Single(Builder().Build(events, [], _principals)));

        Assert.Equal("council.open", entry.Header.Kind);
    }

    [Fact]
    public void Local_time_is_the_creation_time_in_the_viewer_zone()
    {
        var events = new[] { Message(1, _alice, _t0, _msgOne) };

        var entry = Assert.Single(Builder().Build(events, [], _principals));

        Assert.Equal(_t0, entry.Header.CreatedAt);
        Assert.Equal(_t0.ToOffset(TimeSpan.FromHours(10)), entry.Header.LocalTime);
    }

    [Fact]
    public void First_entry_opens_a_date_separator()
    {
        var events = new[] { Message(1, _alice, _t0, _msgOne) };

        var entry = Assert.Single(Builder().Build(events, [], _principals));

        Assert.Equal(new DateOnly(2026, 10, 25), entry.Header.DateSeparator);
    }

    [Fact]
    public void Date_separator_follows_the_viewer_local_day_not_utc()
    {
        // 13:30 UTC is 23:30 local on the 25th; 14:30 UTC is 00:30 local on the 26th.
        var events = new[]
        {
            Message(1, _alice, new DateTimeOffset(2026, 10, 25, 13, 30, 0, TimeSpan.Zero), _msgOne),
            Message(2, _alice, new DateTimeOffset(2026, 10, 25, 14, 30, 0, TimeSpan.Zero), _msgTwo),
        };

        var entries = Builder().Build(events, [], _principals);

        Assert.Equal(new DateOnly(2026, 10, 25), entries[0].Header.DateSeparator);
        Assert.Equal(new DateOnly(2026, 10, 26), entries[1].Header.DateSeparator);
    }

    [Fact]
    public void Same_day_entries_get_no_separator()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Message(2, _bob, _t0.AddHours(1), _msgTwo),
        };

        var entries = Builder().Build(events, [], _principals);

        Assert.Null(entries[1].Header.DateSeparator);
    }

    [Fact]
    public void Same_author_within_the_window_continues_the_group()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Message(2, _alice, _t0.AddMinutes(4), _msgTwo),
        };

        var entries = Builder().Build(events, [], _principals);

        Assert.False(entries[0].Header.IsContinuation);
        Assert.True(entries[1].Header.IsContinuation);
    }

    [Fact]
    public void Gap_of_exactly_the_default_window_still_groups()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Message(2, _alice, _t0.Add(GcEntryBuilder.DefaultGroupWindow), _msgTwo),
        };

        Assert.True(Builder().Build(events, [], _principals)[1].Header.IsContinuation);
    }

    [Fact]
    public void Gap_just_over_the_default_window_breaks_the_group()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Message(2, _alice, _t0.Add(GcEntryBuilder.DefaultGroupWindow).AddSeconds(1), _msgTwo),
        };

        Assert.False(Builder().Build(events, [], _principals)[1].Header.IsContinuation);
    }

    [Fact]
    public void A_different_author_breaks_the_group()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Message(2, _bob, _t0.AddMinutes(1), _msgTwo),
            Message(3, _alice, _t0.AddMinutes(2), Guid.NewGuid()),
        };

        var entries = Builder().Build(events, [], _principals);

        Assert.False(entries[1].Header.IsContinuation);
        Assert.False(entries[2].Header.IsContinuation);
    }

    [Fact]
    public void A_date_separator_breaks_the_group_even_for_the_same_author()
    {
        var events = new[]
        {
            // Local midnight in +10 is 14:00 UTC: 23:58 on the 25th, then 00:01 on the 26th, three minutes apart.
            Message(1, _alice, new DateTimeOffset(2026, 10, 25, 13, 58, 0, TimeSpan.Zero), _msgOne),
            Message(2, _alice, new DateTimeOffset(2026, 10, 25, 14, 1, 0, TimeSpan.Zero), _msgTwo),
        };

        var second = Builder().Build(events, [], _principals)[1];

        Assert.NotNull(second.Header.DateSeparator);
        Assert.False(second.Header.IsContinuation);
    }

    [Fact]
    public void Edits_do_not_break_a_group()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Event(2, GcEventKinds.MessageEdit, _alice, _t0.AddMinutes(1), _msgOne, version: 2),
            Message(3, _alice, _t0.AddMinutes(2), _msgTwo),
        };

        var entries = Builder().Build(events, [], _principals);

        Assert.True(entries[1].Header.IsContinuation);
    }

    [Fact]
    public void A_custom_window_replaces_the_default()
    {
        var events = new[]
        {
            Message(1, _alice, _t0, _msgOne),
            Message(2, _alice, _t0.AddMinutes(2), _msgTwo),
        };

        Assert.False(Builder(TimeSpan.FromMinutes(1)).Build(events, [], _principals)[1].Header.IsContinuation);
    }

    [Fact]
    public void Constructor_rejects_null_zone_and_negative_window()
    {
        Assert.Throws<ArgumentNullException>(() => new GcEntryBuilder(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GcEntryBuilder(_zone, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Build_rejects_null_inputs()
    {
        var builder = Builder();

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!, [], _principals));
        Assert.Throws<ArgumentNullException>(() => builder.Build([], null!, _principals));
        Assert.Throws<ArgumentNullException>(() => builder.Build([], [], null!));
    }
}
