using Bazaar.Client.Core.Input;
using Bazaar.Ex;

namespace Bazaar.Ex.Tests;

public class MnemonicResolverTests
{
    private static MnemonicAssignment Assign(string command, string mnemonic, MnemonicTier tier, string source = "test", bool alias = false) =>
        new(command, command, mnemonic, tier, source, alias);

    [Fact]
    public void First_party_defaults_resolve_clean_and_build_a_valid_table()
    {
        var resolution = MnemonicResolver.Resolve(NavigationDefaults.Assignments);

        Assert.True(resolution.IsValid, string.Join("; ", resolution.Errors));
        Assert.Empty(resolution.Notices);
        Assert.NotNull(resolution.BuildTable([KeyStroke.OfChar('p')]));
    }

    [Fact]
    public void Person_remap_moves_a_command_and_the_default_is_dropped_silently()
    {
        var assignments = NavigationDefaults.Assignments.Concat(
            [Assign(NavigationDefaults.GcFirstCommandId, "gf", MnemonicTier.Person)]);

        var resolution = MnemonicResolver.Resolve(assignments);

        Assert.True(resolution.IsValid);
        Assert.Contains(resolution.Effective, a => a.Mnemonic == "gf" && a.CommandId == NavigationDefaults.GcFirstCommandId);
        Assert.DoesNotContain(resolution.Effective, a => a.Mnemonic == "ngc1");
        Assert.Empty(resolution.Notices);
    }

    [Fact]
    public void Person_cannot_take_a_company_locked_mnemonic()
    {
        var assignments = new[]
        {
            Assign("nav.a", "xa", MnemonicTier.CompanyLocked, "company"),
            Assign("nav.b", "xa", MnemonicTier.Person),
        };

        var resolution = MnemonicResolver.Resolve(assignments);

        Assert.True(resolution.IsValid);
        Assert.Equal("nav.a", Assert.Single(resolution.Effective).CommandId);
        var notice = Assert.Single(resolution.Notices);
        Assert.Contains("company", notice.Message);
    }

    [Fact]
    public void Owner_remap_overrides_a_company_lock()
    {
        var assignments = new[]
        {
            Assign("nav.settings", "settings", MnemonicTier.CompanyLocked, "company"),
            Assign("nav.constellation", "settings", MnemonicTier.Person),
        };

        var resolution = MnemonicResolver.Resolve(assignments, actorIsOwner: true);

        Assert.True(resolution.IsValid);
        Assert.Equal("nav.constellation", Assert.Single(resolution.Effective).CommandId);
        Assert.Single(resolution.Notices);
    }

    [Fact]
    public void Owner_override_of_a_lock_is_not_a_same_tier_clash()
    {
        var assignments = new[]
        {
            Assign("nav.settings", "settings", MnemonicTier.CompanyLocked, "company"),
            Assign("nav.constellation", "settings", MnemonicTier.Person),
        };

        var resolution = MnemonicResolver.Resolve(assignments, actorIsOwner: false);

        Assert.DoesNotContain(resolution.Errors, e => e.Contains("same scope"));
    }

    [Fact]
    public void Two_locks_on_one_mnemonic_are_an_error()
    {
        var assignments = new[]
        {
            Assign("nav.a", "dup", MnemonicTier.CompanyLocked, "company"),
            Assign("nav.b", "dup", MnemonicTier.CompanyLocked, "company"),
        };

        var resolution = MnemonicResolver.Resolve(assignments);

        Assert.False(resolution.IsValid);
        Assert.Contains(resolution.Errors, e => e.Contains("same scope"));
        Assert.Null(resolution.BuildTable([]));
    }

    [Fact]
    public void Two_person_remaps_onto_one_mnemonic_are_an_error()
    {
        var assignments = new[]
        {
            Assign("nav.a", "dup", MnemonicTier.Person),
            Assign("nav.b", "dup", MnemonicTier.Person),
        };

        Assert.False(MnemonicResolver.Resolve(assignments).IsValid);
    }

    [Fact]
    public void A_module_default_loses_to_a_first_party_default_with_a_notice()
    {
        var assignments = NavigationDefaults.Assignments.Concat(
            [Assign("mod.gc", "ngc1", MnemonicTier.Module, "module:gc-extras")]);

        var resolution = MnemonicResolver.Resolve(assignments);

        Assert.True(resolution.IsValid);
        Assert.Equal(NavigationDefaults.GcFirstCommandId, resolution.Effective.Single(a => a.Mnemonic == "ngc1").CommandId);
        Assert.Contains(resolution.Notices, n => n.Message.Contains("module:gc-extras"));
    }

    [Fact]
    public void Two_module_defaults_on_one_mnemonic_are_an_error()
    {
        var assignments = new[]
        {
            Assign("mod.a", "nx", MnemonicTier.Module, "module:a"),
            Assign("mod.b", "nx", MnemonicTier.Module, "module:b"),
        };

        Assert.False(MnemonicResolver.Resolve(assignments).IsValid);
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("ifoo")]
    [InlineData("")]
    public void Invalid_mnemonics_are_errors(string mnemonic)
    {
        var resolution = MnemonicResolver.Resolve([Assign("nav.bad", mnemonic, MnemonicTier.Person)]);

        Assert.False(resolution.IsValid);
        Assert.Empty(resolution.Effective);
    }

    [Fact]
    public void An_alias_adds_a_second_mnemonic_for_the_same_command()
    {
        var resolution = MnemonicResolver.Resolve(NavigationDefaults.Assignments);

        Assert.True(resolution.IsValid);
        Assert.Contains(resolution.Effective, a => a.Mnemonic == "ryuseifuda" && a.CommandId == NavigationDefaults.RyuseiBoardCommandId);
        Assert.Contains(resolution.Effective, a => a.Mnemonic == "kanban" && a.Alias);
    }

    [Fact]
    public void Notice_log_writes_each_notice_once()
    {
        var written = new List<string>();
        var log = new MnemonicNoticeLog(written.Add);
        var notices = MnemonicResolver.Resolve(
            NavigationDefaults.Assignments.Concat([Assign("mod.gc", "ngc1", MnemonicTier.Module, "module:x")])).Notices;

        Assert.Equal(1, log.Record(notices));
        Assert.Equal(0, log.Record(notices));
        Assert.Single(written);
    }
}
