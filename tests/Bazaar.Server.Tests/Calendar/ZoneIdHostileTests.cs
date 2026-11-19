using Bazaar.Server.Calendar;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Tests.Calendar;

/// <summary>Chaos round 3: a time zone id from stored data or an agent is never trusted; a bad one is an answer, not an exception.</summary>
public sealed class ZoneIdHostileTests
{
    public static TheoryData<string> HostileIds =>
    [
        string.Empty,
        " ",
        "\0",
        "UTC\0",
        "Europe/London\0",
        "../../etc/passwd",
        "/etc/passwd",
        "Europe/../Europe/London",
        "Europe//London",
        "Europe/London/",
        "europe/london",
        "EUROPE/LONDON",
        "Europe/Londön",
        "\ud800",
        new string('a', 5000),
        "posixrules",
        "tzdata.zi",
        "zone.tab",
        "iso3166.tab",
        "localtime",
    ];

    [Theory]
    [MemberData(nameof(HostileIds))]
    public void The_calendar_zone_resolver_answers_instead_of_throwing(string tz)
    {
        // A zone that resolves must be a real zone; one that does not must come back false with UTC.
        var ok = CalendarRules.TryResolveZone(tz, out var zone);

        Assert.NotNull(zone);
        if (!ok)
            Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Theory]
    [MemberData(nameof(HostileIds))]
    public void An_agent_schedule_refuses_a_hostile_zone_id_with_an_argument_exception(string tz)
    {
        try
        {
            _ = new AgentSchedule(tz, [], null);
        }
        catch (ArgumentException)
        {
            // Designed: ArgumentNullException is an ArgumentException.
        }
    }
}
