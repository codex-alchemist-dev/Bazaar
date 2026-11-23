namespace Bazaar.Server.Calendar.Tests;

/// <summary>Pure tests of the title redaction (doc 24, "Calendar event visibility"): three levels against four kinds of viewer.</summary>
public sealed class CalendarVisibilityRulesTests
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _other = Guid.Parse("00000000-0000-7000-8000-0000000000b2");

    private static readonly CalendarViewer _companyOwner = new(Guid.Parse("00000000-0000-7000-8000-0000000000c3"), IsCompanyOwner: true);
    private static readonly CalendarViewer _eventAgent = new(_agent, IsCompanyOwner: false);
    private static readonly CalendarViewer _otherAgent = new(_other, IsCompanyOwner: false);

    private static CalendarEventRecord Event(CalendarVisibility visibility, Guid? agent = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = CalendarKind.Planning,
        TitleKeyId = Guid.NewGuid(),
        TitleCiphertext = [1],
        StartsAt = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero),
        EndsAt = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero),
        AllDay = false,
        Tz = "UTC",
        AgentId = agent,
        AffectsAvailability = false,
        Status = CalendarStatus.Confirmed,
        Version = 1,
        Visibility = visibility,
    };

    public static TheoryData<CalendarVisibility, string, bool> Matrix => new()
    {
        // public: everyone
        { CalendarVisibility.Public, "owner", true },
        { CalendarVisibility.Public, "agent", true },
        { CalendarVisibility.Public, "other", true },
        { CalendarVisibility.Public, "anonymous", true },

        // attendees: the company owner and the event's agent
        { CalendarVisibility.Attendees, "owner", true },
        { CalendarVisibility.Attendees, "agent", true },
        { CalendarVisibility.Attendees, "other", false },
        { CalendarVisibility.Attendees, "anonymous", false },

        // owner: the company owner and the event's own agent
        { CalendarVisibility.Owner, "owner", true },
        { CalendarVisibility.Owner, "agent", true },
        { CalendarVisibility.Owner, "other", false },
        { CalendarVisibility.Owner, "anonymous", false },
    };

    private static CalendarViewer Viewer(string kind) => kind switch
    {
        "owner" => _companyOwner,
        "agent" => _eventAgent,
        "other" => _otherAgent,
        _ => CalendarViewer.Anonymous,
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Title_is_shown_or_redacted_per_level_and_viewer(CalendarVisibility visibility, string viewer, bool shown)
    {
        var record = Event(visibility, _agent);

        Assert.Equal(shown, CalendarVisibilityRules.CanSeeTitle(record, Viewer(viewer)));
        Assert.Equal(shown ? "Dentist" : CalendarVisibilityRules.RedactedTitle, CalendarVisibilityRules.TitleFor(record, Viewer(viewer), "Dentist"));
    }

    [Fact]
    public void Redacted_title_is_the_doc_text()
    {
        Assert.Equal("(private)", CalendarVisibilityRules.RedactedTitle);
    }

    [Theory]
    [InlineData(CalendarVisibility.Attendees)]
    [InlineData(CalendarVisibility.Owner)]
    public void An_event_with_no_agent_is_private_to_the_company_owner(CalendarVisibility visibility)
    {
        var record = Event(visibility, agent: null);

        Assert.True(CalendarVisibilityRules.CanSeeTitle(record, _companyOwner));
        Assert.False(CalendarVisibilityRules.CanSeeTitle(record, _otherAgent));

        // The anonymous viewer carries the empty id, which must not match an event that has no agent.
        Assert.False(CalendarVisibilityRules.CanSeeTitle(record, CalendarViewer.Anonymous));
    }

    [Fact]
    public void Anonymous_viewer_is_the_least_privileged()
    {
        Assert.Equal(Guid.Empty, CalendarViewer.Anonymous.Id);
        Assert.False(CalendarViewer.Anonymous.IsCompanyOwner);
    }

    [Theory]
    [InlineData(CalendarKind.Vacation, CalendarVisibility.Attendees)]
    [InlineData(CalendarKind.Planning, CalendarVisibility.Public)]
    [InlineData(CalendarKind.Release, CalendarVisibility.Public)]
    [InlineData(CalendarKind.Freeze, CalendarVisibility.Public)]
    [InlineData(CalendarKind.Milestone, CalendarVisibility.Public)]
    public void Default_visibility_follows_the_doc(CalendarKind kind, CalendarVisibility expected)
    {
        Assert.Equal(expected, CalendarRules.DefaultVisibility(kind));
    }

    [Theory]
    [InlineData(CalendarVisibility.Public, "public")]
    [InlineData(CalendarVisibility.Attendees, "attendees")]
    [InlineData(CalendarVisibility.Owner, "owner")]
    public void Visibility_text_round_trips(CalendarVisibility visibility, string text)
    {
        Assert.Equal(text, CalendarRules.ToSql(visibility));
        Assert.Equal(visibility, CalendarRules.VisibilityFromSql(text));
    }

    [Fact]
    public void Unknown_stored_visibility_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CalendarRules.VisibilityFromSql("secret"));
    }
}
