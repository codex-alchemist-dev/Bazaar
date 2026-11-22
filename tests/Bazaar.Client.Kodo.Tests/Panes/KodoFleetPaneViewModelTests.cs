using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Kodo.Tests.Panes;

public class KodoFleetPaneViewModelTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static KodoFleetDeviceInput Device(string id, string name, string version = "0.2.0", DateTimeOffset? seen = null, double? crash = 0, string stream = "connected", string channel = "0.2.0+abc1234") =>
        new(id, name, version, channel, seen ?? _now, crash, stream);

    private static KodoFleetPaneViewModel Make(IKodoPermissions? permissions = null) =>
        new(new FakeTimeProvider(_now), permissions ?? new KodoAllPermissions());

    [Theory]
    [InlineData("0.1.0", "0.2.0", KodoVersionStanding.Behind)]
    [InlineData("0.2.0+aaa", "0.2.0+bbb", KodoVersionStanding.Current)]
    [InlineData("v0.3.0", "0.2.0", KodoVersionStanding.Ahead)]
    [InlineData("0.2.0-beta", "0.2.0", KodoVersionStanding.Behind)]
    [InlineData("0.2.0", "0.2.0-beta", KodoVersionStanding.Ahead)]
    [InlineData("junk", "0.2.0", KodoVersionStanding.Unknown)]
    [InlineData("0.2.0", "", KodoVersionStanding.Unknown)]
    public void Version_standing_compares_the_core_and_ignores_build_metadata(string version, string channel, KodoVersionStanding expected) =>
        Assert.Equal(expected, KodoFleetPaneViewModel.Standing(version, channel));

    [Fact]
    public void A_row_shows_version_against_channel_last_seen_crash_rate_and_stream_state()
    {
        var model = Make();
        model.SetDevices([Device("d1", "Desk", "0.1.0", _now - TimeSpan.FromMinutes(20), 2.5, "disconnected")]);

        var row = Assert.Single(model.Build().Rows);

        Assert.Equal("0.1.0 (behind 0.2.0+abc1234)", row.VersionText);
        Assert.Equal("Last seen 20 min ago", row.LastSeenText);
        Assert.True(row.IsStale);
        Assert.Equal("2.5 crashes per 100 sessions", row.CrashRateText);
        Assert.Equal(KodoFleetStreamState.Disconnected, row.StreamState);
        Assert.Contains("Desk", row.AccessibleName);
    }

    [Fact]
    public void Missing_data_is_said_not_guessed()
    {
        var model = Make();
        model.SetDevices([new KodoFleetDeviceInput("d1", "Desk", "0.2.0", "", null, null, "weird")]);

        var row = Assert.Single(model.Build().Rows);

        Assert.Equal("Never seen", row.LastSeenText);
        Assert.Equal("Crash rate not reported", row.CrashRateText);
        Assert.Equal(KodoFleetStreamState.Unknown, row.StreamState);
        Assert.Equal(KodoVersionStanding.Unknown, row.Standing);
    }

    [Fact]
    public void Attention_order_is_stream_then_crash_rate_then_behind_then_age_then_name()
    {
        var model = Make();
        model.SetDevices(
        [
            Device("1", "ok", crash: 0),
            Device("2", "crashy", crash: 9),
            Device("3", "dark", stream: "unobserved"),
            Device("4", "off", stream: "disconnected"),
            Device("5", "behind", version: "0.1.0"),
        ]);

        Assert.Equal(["dark", "off", "crashy", "behind", "ok"], model.Build().Rows.Select(r => r.DeviceName));
    }

    [Fact]
    public void Ties_break_by_name_then_id_so_order_is_stable()
    {
        var model = Make();
        var devices = new[] { Device("2", "same"), Device("1", "same"), Device("3", "Alpha") };
        model.SetDevices(devices);
        var first = model.Build().Rows.Select(r => r.DeviceId).ToList();
        model.SetDevices(devices.Reverse());

        Assert.Equal(["3", "1", "2"], first);
        Assert.Equal(first, model.Build().Rows.Select(r => r.DeviceId));
    }

    [Fact]
    public void Sort_by_last_seen_puts_never_seen_then_oldest_first()
    {
        var model = Make();
        model.Sort = KodoFleetSort.LastSeen;
        model.SetDevices(
        [
            Device("1", "new", seen: _now),
            new KodoFleetDeviceInput("2", "never", "0.2.0", "0.2.0", null, 0, "connected"),
            Device("3", "old", seen: _now - TimeSpan.FromHours(2)),
        ]);

        Assert.Equal(["never", "old", "new"], model.Build().Rows.Select(r => r.DeviceName));
    }

    [Fact]
    public void Sort_by_crash_rate_is_highest_first_and_unreported_last()
    {
        var model = Make();
        model.Sort = KodoFleetSort.CrashRate;
        model.SetDevices([Device("1", "a", crash: 1), Device("2", "b", crash: null), Device("3", "c", crash: 5)]);

        Assert.Equal(["c", "a", "b"], model.Build().Rows.Select(r => r.DeviceName));
    }

    [Fact]
    public void Sort_by_name_is_alphabetical()
    {
        var model = Make();
        model.Sort = KodoFleetSort.Name;
        model.SetDevices([Device("1", "zed"), Device("2", "Amy"), Device("3", "mid")]);

        Assert.Equal(["Amy", "mid", "zed"], model.Build().Rows.Select(r => r.DeviceName));
    }

    [Fact]
    public void An_empty_fleet_says_so_and_a_viewer_without_the_grant_sees_nothing()
    {
        Assert.Equal("No devices reported.", Make().Build().EmptyText);

        var denied = Make(new KodoNoPermissions());
        denied.SetDevices([Device("1", "x")]);
        var panel = denied.Build();

        Assert.False(panel.Permitted);
        Assert.Empty(panel.Rows);
    }
}
