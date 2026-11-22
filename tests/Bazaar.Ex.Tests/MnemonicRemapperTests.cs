using Bazaar.Client.Input;
using Bazaar.Ex;

namespace Bazaar.Ex.Tests;

public class MnemonicRemapperTests
{
    private static readonly KeyStroke[] _palette = [KeyStroke.OfChar('p')];

    private static List<MnemonicAssignment> Defaults() => [.. NavigationDefaults.Assignments];

    private static MnemonicRemapResult Remap(
        string commandId,
        string mnemonic,
        MnemonicOverrideDocument? current = null,
        IEnumerable<MnemonicAssignment>? defaults = null,
        bool owner = false) =>
        MnemonicRemapper.TryRemap(defaults ?? Defaults(), current ?? MnemonicOverrideDocument.Empty, commandId, mnemonic, _palette, owner);

    [Fact]
    public void A_free_mnemonic_is_accepted_and_becomes_the_effective_one()
    {
        var result = Remap(NavigationDefaults.SettingsCommandId, "ss");

        Assert.True(result.Accepted);
        var titles = Defaults()
            .GroupBy(a => a.CommandId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        var effective = MnemonicResolver.Resolve([.. Defaults(), .. FileMnemonicOverrideStore.ToAssignments(result.Document, titles)]).Effective;
        Assert.Equal("ss", effective.Single(a => a.CommandId == NavigationDefaults.SettingsCommandId && !a.Alias).Mnemonic);
    }

    [Fact]
    public void An_unknown_command_is_rejected()
    {
        var result = Remap("nav.nope", "zz");

        Assert.False(result.Accepted);
        Assert.Contains("not a command", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a-b")]
    [InlineData("a,b")]
    public void An_invalid_mnemonic_is_rejected_with_the_reason(string text)
    {
        var result = Remap(NavigationDefaults.SettingsCommandId, text);

        Assert.False(result.Accepted);
        Assert.NotNull(result.Reason);
        Assert.Empty(result.Document.Overrides);
    }

    [Fact]
    public void A_mnemonic_starting_with_the_reserved_insert_key_is_rejected()
    {
        var result = Remap(NavigationDefaults.SettingsCommandId, "ix");

        Assert.False(result.Accepted);
        Assert.Contains("reserved", result.Reason);
    }

    [Fact]
    public void A_mnemonic_used_by_another_command_is_rejected_and_names_it()
    {
        var result = Remap(NavigationDefaults.SettingsCommandId, "unread");

        Assert.False(result.Accepted);
        Assert.Contains("already used", result.Reason);
    }

    [Fact]
    public void A_locked_command_is_rejected_for_a_person_but_allowed_for_the_owner()
    {
        var defaults = Defaults();
        defaults.Add(new MnemonicAssignment("company.lock", "Company lock", "lockme", MnemonicTier.CompanyLocked, "company"));

        var person = Remap("company.lock", "lm", defaults: defaults);
        var owner = Remap("company.lock", "lm", defaults: defaults, owner: true);

        Assert.False(person.Accepted);
        Assert.Contains("locked", person.Reason);
        Assert.True(owner.Accepted);
    }

    [Fact]
    public void Remapping_to_the_default_drops_the_override()
    {
        var first = Remap(NavigationDefaults.SettingsCommandId, "ss");
        Assert.Single(first.Document.Overrides);

        var back = Remap(NavigationDefaults.SettingsCommandId, "settings", first.Document);

        Assert.True(back.Accepted);
        Assert.Empty(back.Document.Overrides);
    }

    [Fact]
    public void Remapping_again_replaces_the_earlier_remap()
    {
        var first = Remap(NavigationDefaults.SettingsCommandId, "ss");
        var second = Remap(NavigationDefaults.SettingsCommandId, "st", first.Document);

        Assert.True(second.Accepted);
        Assert.Equal("st", Assert.Single(second.Document.Overrides).Mnemonic);
    }

    [Fact]
    public void A_remapped_mnemonic_cannot_be_taken_by_a_second_command()
    {
        var first = Remap(NavigationDefaults.SettingsCommandId, "ss");
        var second = Remap(NavigationDefaults.UnreadCommandId, "ss", first.Document);

        Assert.False(second.Accepted);
        Assert.Contains("already used", second.Reason);
        Assert.Single(second.Document.Overrides);
    }

    [Fact]
    public void Reset_removes_only_that_commands_override()
    {
        var one = Remap(NavigationDefaults.SettingsCommandId, "ss");
        var two = Remap(NavigationDefaults.UnreadCommandId, "uu", one.Document);

        var reset = MnemonicRemapper.Reset(two.Document, NavigationDefaults.SettingsCommandId);

        Assert.True(reset.Accepted);
        Assert.Equal(NavigationDefaults.UnreadCommandId, Assert.Single(reset.Document.Overrides).CommandId);
    }

    [Fact]
    public void Reset_of_a_command_with_no_remap_changes_nothing()
    {
        var reset = MnemonicRemapper.Reset(MnemonicOverrideDocument.Empty, NavigationDefaults.SettingsCommandId);

        Assert.True(reset.Accepted);
        Assert.Empty(reset.Document.Overrides);
    }
}
