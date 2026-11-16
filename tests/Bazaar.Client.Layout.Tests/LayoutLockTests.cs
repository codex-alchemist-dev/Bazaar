using System.Text.Json;
using Bazaar.Client.Layout;

namespace Bazaar.Client.Layout.Tests;

public class LayoutLockTests
{
    private const string _dashConfig = "{\"density\":\"compact\",\"theme\":\"dark\"}";

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static LayoutNode Pane(string kind, string id, string? config = null) =>
        new(null, [], kind, id, config is null ? null : Json(config));

    private static LayoutNode Column(string id, params LayoutNode[] children) =>
        new("column", children, null, id, null);

    private static LayoutSlot Phone(LayoutNode root) => new("phone", "single", "folded", root);

    private static LayoutSlot Desktop(LayoutNode root) => new("desktop", "single", "unfolded", root);

    /// <summary>Company: phone has gc-list and dash (with config) under root. Locks are supplied per test.</summary>
    private static LayoutDocument Company(params string[] locks) => new(1, "Company", locks,
    [
        Phone(Column("root", Pane("bazaar.gc", "gc-list"), Pane("bazaar.dashboard", "dash", _dashConfig))),
    ]);

    private static string WithLock(string entry) =>
        "{\"schemaVersion\":1,\"name\":\"C\",\"locks\":[\"" + entry
        + "\"],\"slots\":[{\"deviceClass\":\"phone\",\"display\":\"*\",\"posture\":\"*\",\"root\":{\"paneKind\":\"bazaar.gc\"}}]}";

    private static LayoutDocument Person(params LayoutSlot[] slots) => new(1, "Mine", [], slots);

    private static LayoutNode PhoneRoot(LayoutMergeResult result) =>
        LayoutResolver.Select(result.Effective, "phone", "single", "folded")!.Root;

    private static List<string> Codes(LayoutMergeResult result) => result.Issues.Select(i => i.Code).ToList();

    [Theory]
    [InlineData("node:gc-list")]
    [InlineData("setting:dash/density")]
    public void Lock_entries_in_the_new_forms_validate_as_locks(string entry)
    {
        LayoutLoadResult result = LayoutValidator.Validate(WithLock(entry));

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}")));
        Assert.Equal([entry], result.Document!.Locks);
    }

    [Theory]
    [InlineData("node:")]
    [InlineData("node:a/b")]
    [InlineData("node: ")]
    [InlineData("setting:dash")]
    [InlineData("setting:/density")]
    [InlineData("setting:dash/")]
    [InlineData("setting:dash/density/extra")]
    public void Malformed_lock_entries_are_rejected_with_bad_lock(string entry)
    {
        LayoutLoadResult result = LayoutValidator.Validate(WithLock(entry));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "/locks/0" && i.Code == "bad-lock");
    }

    [Fact]
    public void Parse_helpers_return_the_pane_id_and_key()
    {
        Assert.True(LayoutResolver.TryParseNodeLock("node:gc-list", out string node));
        Assert.Equal("gc-list", node);
        Assert.True(LayoutResolver.TryParseSettingLock("setting:dash/density", out string pane, out string key));
        Assert.Equal(("dash", "density"), (pane, key));
        Assert.False(LayoutResolver.TryParseNodeLock("phone/*/*", out _));
        Assert.False(LayoutResolver.TryParseSettingLock("node:gc-list", out _, out _));
    }

    [Fact]
    public void Person_cannot_remove_a_locked_node()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.dashboard", "dash", _dashConfig))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list"), person, actorIsOwner: false);

        Assert.Equal(["locked-node"], Codes(result));
        Assert.Equal("/slots/0", result.Issues[0].Path);
        Assert.Equal(2, PhoneRoot(result).Children.Count);
    }

    [Fact]
    public void Person_cannot_change_the_kind_of_a_locked_node()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.dashboard", "gc-list"), Pane("bazaar.dashboard", "dash", _dashConfig))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list"), person, actorIsOwner: false);

        Assert.Equal(["locked-node"], Codes(result));
    }

    [Fact]
    public void Person_cannot_move_a_locked_node_to_another_container()
    {
        var person = Person(Phone(Column("root",
            Column("side", Pane("bazaar.gc", "gc-list")),
            Pane("bazaar.dashboard", "dash", _dashConfig))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list"), person, actorIsOwner: false);

        Assert.Equal(["locked-node"], Codes(result));
    }

    [Fact]
    public void Person_cannot_add_a_child_to_a_locked_container()
    {
        var company = new LayoutDocument(1, "Company", ["node:root"], [Phone(Column("root", Pane("bazaar.gc", "gc-list")))]);
        var person = Person(Phone(Column("root", Pane("bazaar.gc", "gc-list"), Pane("bazaar.vault", "vault"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Equal(["locked-node"], Codes(result));
    }

    [Fact]
    public void Person_may_change_unlocked_siblings_of_a_locked_node()
    {
        var person = Person(Phone(Column("root",
            Pane("bazaar.gc", "gc-list"),
            Pane("bazaar.dashboard", "dash", _dashConfig),
            Pane("bazaar.vault", "vault"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list"), person, actorIsOwner: false);

        Assert.Empty(result.Issues);
        Assert.Equal(3, PhoneRoot(result).Children.Count);
    }

    [Fact]
    public void Locked_node_is_protected_in_a_new_slot_the_person_adds()
    {
        var person = Person(
            Desktop(Column("root", Pane("bazaar.dashboard", "gc-list"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list"), person, actorIsOwner: false);

        Assert.Equal(["locked-node"], Codes(result));
        Assert.Null(LayoutResolver.Select(result.Effective, "desktop", "single", "unfolded"));
    }

    [Fact]
    public void New_slot_without_the_locked_node_is_accepted()
    {
        var person = Person(Desktop(Column("root", Pane("bazaar.vault", "vault"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list"), person, actorIsOwner: false);

        Assert.Empty(result.Issues);
        Assert.NotNull(LayoutResolver.Select(result.Effective, "desktop", "single", "unfolded"));
    }

    [Fact]
    public void Locked_node_matches_the_company_copy_in_each_slot()
    {
        // The node appears under a different parent in each company slot. A person copy of either slot is accepted.
        var company = new LayoutDocument(1, "Company", ["node:gc-list"],
        [
            Phone(Column("root", Pane("bazaar.gc", "gc-list"))),
            Desktop(Column("side", Pane("bazaar.gc", "gc-list"))),
        ]);
        var person = Person(
            Phone(Column("root", Pane("bazaar.gc", "gc-list"))),
            Desktop(Column("side", Pane("bazaar.gc", "gc-list"), Pane("bazaar.vault", "vault"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Changing_a_locked_node_in_the_slot_it_replaces_is_rejected_even_if_moved_to_another_slot()
    {
        var company = new LayoutDocument(1, "Company", ["node:gc-list"],
        [
            Phone(Column("root", Pane("bazaar.gc", "gc-list"))),
            Desktop(Column("side", Pane("bazaar.gc", "gc-list"))),
        ]);
        var person = Person(Phone(Column("side", Pane("bazaar.gc", "gc-list"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Equal(["locked-node"], Codes(result));
    }

    [Fact]
    public void Person_cannot_change_a_locked_setting()
    {
        var person = Person(Phone(Column("root",
            Pane("bazaar.gc", "gc-list"),
            Pane("bazaar.dashboard", "dash", "{\"density\":\"comfortable\",\"theme\":\"dark\"}"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("setting:dash/density"), person, actorIsOwner: false);

        Assert.Equal(["locked-setting"], Codes(result));
    }

    [Fact]
    public void Person_cannot_remove_a_locked_setting()
    {
        var person = Person(Phone(Column("root",
            Pane("bazaar.gc", "gc-list"),
            Pane("bazaar.dashboard", "dash", "{\"theme\":\"dark\"}"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("setting:dash/density"), person, actorIsOwner: false);

        Assert.Equal(["locked-setting"], Codes(result));
    }

    [Fact]
    public void Person_cannot_add_a_locked_setting_the_company_does_not_set()
    {
        var company = new LayoutDocument(1, "Company", ["setting:vault/density"],
            [Phone(Column("root", Pane("bazaar.vault", "vault")))]);
        var person = Person(Phone(Column("root", Pane("bazaar.vault", "vault", "{\"density\":\"compact\"}"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(company, person, actorIsOwner: false);

        Assert.Equal(["locked-setting"], Codes(result));
    }

    [Fact]
    public void Person_may_change_unlocked_keys_of_a_pane_with_a_locked_setting()
    {
        var person = Person(Phone(Column("root",
            Pane("bazaar.gc", "gc-list"),
            Pane("bazaar.dashboard", "dash", "{\"density\":\"compact\",\"theme\":\"light\",\"extra\":1}"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("setting:dash/density"), person, actorIsOwner: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Removing_a_pane_with_a_locked_setting_is_rejected()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.gc", "gc-list"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("setting:dash/density"), person, actorIsOwner: false);

        Assert.Equal(["locked-setting"], Codes(result));
    }

    [Fact]
    public void Setting_lock_compares_values_not_formatting()
    {
        var person = Person(Phone(Column("root",
            Pane("bazaar.gc", "gc-list"),
            Pane("bazaar.dashboard", "dash", "{ \"density\" : \"compact\", \"theme\": \"dark\" }"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("setting:dash/density"), person, actorIsOwner: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Each_broken_lock_is_reported_and_the_slot_is_not_applied()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.dashboard", "dash", "{\"density\":\"comfortable\"}"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list", "setting:dash/density"), person, actorIsOwner: false);

        Assert.Equal(["locked-node", "locked-setting"], Codes(result));
        Assert.Equal(2, PhoneRoot(result).Children.Count);
    }

    [Fact]
    public void Locks_on_unknown_panes_have_no_effect()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.vault", "vault"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:missing", "setting:missing/density"), person, actorIsOwner: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Owner_overrides_node_and_setting_locks()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.dashboard", "dash", "{\"density\":\"comfortable\"}"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("node:gc-list", "setting:dash/density"), person, actorIsOwner: true);

        Assert.Empty(result.Issues);
        Assert.Single(PhoneRoot(result).Children);
    }

    [Fact]
    public void Slot_lock_still_rejects_a_person_slot_before_node_checks()
    {
        var person = Person(Phone(Column("root", Pane("bazaar.vault", "vault"))));

        LayoutMergeResult result = LayoutScopeMerger.Merge(Company("phone/*/*", "node:gc-list"), person, actorIsOwner: false);

        Assert.Equal(["locked-slot"], Codes(result));
    }
}
