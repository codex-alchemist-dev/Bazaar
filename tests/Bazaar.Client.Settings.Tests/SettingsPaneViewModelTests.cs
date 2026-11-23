using System.Text.Json;
using Bazaar.Contracts.Panes;
using Bazaar.Server.Settings;
using static Bazaar.Client.Settings.Tests.SettingsTestSupport;

namespace Bazaar.Client.Settings.Tests;

public class SettingsPaneViewModelTests
{
    private static PaneDescriptor Descriptor() => new(PaneKinds.Settings, "Settings", string.Empty, JsonElement.Parse("{}"));

    private static async Task<(SettingsPaneViewModel Pane, FakeClient Client)> LoadAsync(SettingActor actor, SettingsChangeResult? reply = null, params (SettingDefinition, string?)[] stored)
    {
        var client = new FakeClient(Snapshot(actor, stored), reply);
        var pane = new SettingsPaneViewModel(Descriptor(), client);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        return (pane, client);
    }

    private static SettingRowViewModel Row(SettingsPaneViewModel pane, string name) => pane.Rows.Single(r => r.Name == name);

    [Fact]
    public async Task Registry_entries_map_to_rows_grouped_by_area_in_name_order()
    {
        var (pane, _) = await LoadAsync(Operator);

        Assert.Equal(["backup", "host", "mail"], pane.Groups.Select(g => g.Area));
        Assert.Equal(["host.allowed", "host.bind", "host.flag", "host.mode"], pane.Groups[1].Rows.Select(r => r.Name));
        var days = Row(pane, "backup.days");
        Assert.Equal("Days between drills.", days.Description);
        Assert.Equal("7", days.DefaultText);
        Assert.Equal("Install", days.ScopeText);
        Assert.Equal("Operator", days.WhoMayChangeText);
        Assert.True(days.IsDefault);
    }

    [Fact]
    public async Task Stored_value_is_the_current_value_and_the_default_stays_visible()
    {
        var (pane, _) = await LoadAsync(Operator, null, (Days, "30"));

        var days = Row(pane, "backup.days");
        Assert.Equal("30", days.CurrentText);
        Assert.Equal("7", days.DefaultText);
        Assert.False(days.IsDefault);
    }

    [Fact]
    public async Task Editor_kind_follows_the_value_type()
    {
        var (pane, _) = await LoadAsync(Operator);

        Assert.Equal(SettingEditorKind.Toggle, Row(pane, "host.flag").EditorKind);
        Assert.Equal(SettingEditorKind.Choice, Row(pane, "host.mode").EditorKind);
        Assert.Equal(["quiet", "loud_mode"], Row(pane, "host.mode").Choices);
        Assert.Equal(SettingEditorKind.Number, Row(pane, "backup.days").EditorKind);
        Assert.Equal(SettingEditorKind.Text, Row(pane, "host.bind").EditorKind);
        Assert.Equal(SettingEditorKind.ReadOnlyValue, Row(pane, "host.allowed").EditorKind);
        Assert.Equal("a, b", Row(pane, "host.allowed").CurrentText);
    }

    [Fact]
    public async Task Role_without_permission_makes_the_row_read_only_with_the_reason()
    {
        var (pane, client) = await LoadAsync(Operator);

        var name = Row(pane, "mail.name");
        Assert.False(name.CanEdit);
        Assert.Contains("only Owner may change this", name.ReadOnlyReason);
        Assert.Contains("Operator", name.ReadOnlyReason);

        name.Draft = "x";
        await name.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Empty(client.Writes);
        Assert.True(Row(pane, "backup.days").CanEdit);
    }

    [Fact]
    public async Task Actor_with_no_role_cannot_edit_anything()
    {
        var (pane, _) = await LoadAsync(Nobody);

        Assert.All(pane.Rows, r => Assert.False(r.CanEdit));
        Assert.Contains("no role", Row(pane, "host.flag").ReadOnlyReason);
    }

    [Fact]
    public async Task Validation_error_comes_from_the_registry_and_nothing_is_sent()
    {
        var (pane, client) = await LoadAsync(Operator);

        var days = Row(pane, "backup.days");
        days.Draft = "400";
        Assert.Equal("Days must be 1 to 90.", days.Error);
        Assert.False(days.CanSave);

        await days.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Empty(client.Writes);
    }

    [Theory]
    [InlineData("abc", "Enter a whole number.")]
    [InlineData("2.5", "Enter a whole number.")]
    public async Task Text_that_is_not_a_number_is_refused_before_the_registry(string draft, string expected)
    {
        var (pane, _) = await LoadAsync(Operator);

        var days = Row(pane, "backup.days");
        days.Draft = draft;
        Assert.Equal(expected, days.Error);
    }

    [Fact]
    public async Task Valid_change_is_written_in_canonical_json_and_becomes_current()
    {
        var (pane, client) = await LoadAsync(Operator);

        var days = Row(pane, "backup.days");
        days.Draft = " 30 ";
        Assert.Null(days.Error);
        await days.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("backup.days", "30")], client.Writes);
        Assert.Equal("30", days.CurrentText);
        Assert.Equal("Saved.", days.Status);
        Assert.False(days.IsDirty);
    }

    [Fact]
    public async Task Choice_and_toggle_write_the_wire_form()
    {
        var (pane, client) = await LoadAsync(Operator);

        Row(pane, "host.mode").Draft = "loud_mode";
        await Row(pane, "host.mode").SaveAsync(TestContext.Current.CancellationToken);
        Row(pane, "host.flag").Draft = "true";
        await Row(pane, "host.flag").SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("host.mode", "\"loud_mode\""), ("host.flag", "true")], client.Writes);
        Row(pane, "host.mode").Draft = "nope";
        Assert.Contains("Choose one of", Row(pane, "host.mode").Error);
    }

    [Fact]
    public async Task Approval_required_is_marked_and_the_pending_value_is_flagged_but_still_sent_for_the_server_to_hold()
    {
        var held = new SettingsChangeResult(SettingWriteOutcome.PendingApproval, "'host.bind' needs Tier2 approval; the change is held.");
        var (pane, client) = await LoadAsync(Operator, held);

        var bind = Row(pane, "host.bind");
        Assert.True(bind.NeedsApproval);
        Assert.Contains("Tier2 approval", bind.ApprovalText);
        Assert.False(Row(pane, "host.flag").NeedsApproval);

        bind.Draft = "0.0.0.0";
        Assert.Null(bind.Error);
        Assert.Contains("needs Tier2 approval", bind.PendingNote);

        await bind.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("host.bind", "\"0.0.0.0\"")], client.Writes);
        Assert.Equal("127.0.0.1", bind.CurrentText);
        Assert.Equal(held.Reason, bind.Status);
    }

    [Fact]
    public async Task Loopback_value_is_not_flagged_as_needing_approval()
    {
        var (pane, _) = await LoadAsync(Operator, null, (Bind, "\"0.0.0.0\""));

        var bind = Row(pane, "host.bind");
        bind.Draft = "127.0.0.1";
        Assert.Null(bind.PendingNote);
    }

    [Fact]
    public async Task Server_refusal_is_shown_and_the_stored_value_is_unchanged()
    {
        var (pane, _) = await LoadAsync(Operator, new SettingsChangeResult(SettingWriteOutcome.Rejected, "The floor holds."));

        var days = Row(pane, "backup.days");
        days.Draft = "30";
        await days.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("The floor holds.", days.Error);
        Assert.Equal("7", days.CurrentText);
    }

    [Fact]
    public async Task Search_filters_by_name_description_and_area()
    {
        var (pane, _) = await LoadAsync(Operator);

        pane.Search = "drills";
        Assert.Equal(["backup.days"], pane.Groups.SelectMany(g => g.Rows).Select(r => r.Name));

        pane.Search = "HOST bind";
        Assert.Equal(["host.bind"], pane.Groups.SelectMany(g => g.Rows).Select(r => r.Name));

        pane.Search = "zzz";
        Assert.Empty(pane.Groups);
        Assert.Equal("No settings match the search.", pane.StatusText);

        pane.Search = string.Empty;
        Assert.Equal(6, pane.Groups.Sum(g => g.Rows.Count));
    }

    [Fact]
    public async Task Not_connected_client_shows_the_not_connected_line_and_refuses_writes()
    {
        var pane = new SettingsPaneViewModel(Descriptor(), NotConnectedSettingsClient.Instance);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(pane.IsConnected);
        Assert.Equal(SettingsPaneViewModel.NotConnectedText, pane.StatusText);
        Assert.Empty(pane.Groups);

        var result = await NotConnectedSettingsClient.Instance.SetAsync("host.flag", "true", TestContext.Current.CancellationToken);
        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("Not connected", result.Reason);
    }

    [Fact]
    public async Task Revert_restores_the_current_value()
    {
        var (pane, _) = await LoadAsync(Operator);

        var days = Row(pane, "backup.days");
        days.Draft = "30";
        Assert.True(days.IsDirty);
        days.Revert();
        Assert.Equal("7", days.Draft);
        Assert.False(days.IsDirty);
    }
}
