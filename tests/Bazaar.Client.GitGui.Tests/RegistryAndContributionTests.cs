using Bazaar.Client.Commands;
using Bazaar.Client.Input;
using Bazaar.Client.GitGui.Diff;

namespace Bazaar.Client.GitGui.Tests;

public sealed class RegistryAndContributionTests
{
    private static readonly string[] _showCommands = ["git.show-view", "git.show-prs", "git.show-pr", "git.show-diff"];

    [Fact]
    public void Find_is_case_insensitive_and_unknown_is_null()
    {
        var registry = new GitProviderUiRegistry([new FakeProviderUi("github"), new FakeProviderUi("gitlab", "merge request")]);

        Assert.Equal("merge request", registry.Find("GitLab")!.PrNoun);
        Assert.Null(registry.Find("bitbucket"));
        Assert.Null(registry.Find(string.Empty));
        Assert.Equal(["github", "gitlab"], registry.ProviderIds);
    }

    [Fact]
    public void Duplicate_ids_are_rejected_even_across_case()
    {
        Assert.Throws<ArgumentException>(() => new GitProviderUiRegistry([new FakeProviderUi("github"), new FakeProviderUi("GitHub")]));
    }

    [Fact]
    public void Blank_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new GitProviderUiRegistry([new FakeProviderUi(" ")]));
    }

    [Fact]
    public void Module_ids_and_kinds_are_the_four_git_panes()
    {
        Assert.Equal(["git.view", "git.prs", "git.pr", "git.diff"], GitGuiPanes.PaneKinds);
        Assert.Equal(GitGuiPanes.PaneKinds, GitGuiPanes.Modules.Select(m => m.Id));
        Assert.Equal(GitGuiPanes.PaneKinds, Fixtures.Registry(new InMemoryGitGuiClient()).Kinds);
    }

    [Fact]
    public void Constellation_commands_are_contributed()
    {
        var ids = GitGuiPanes.CommandContributions.Select(c => c.Id).ToList();

        Assert.Contains("git.open-in-constellation", ids);
        Assert.Contains("git.open-commit", ids);
        Assert.Equal("ref", GitGuiPanes.CommandContributions.Single(c => c.Id == "git.open-in-constellation").ParameterName);
        Assert.Equal("sha", GitGuiPanes.CommandContributions.Single(c => c.Id == "git.open-commit").ParameterName);
    }

    [Fact]
    public void Every_pane_kind_has_a_show_command()
    {
        var ids = GitGuiPanes.CommandContributions.Select(c => c.Id).ToList();
        Assert.All(_showCommands, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Mnemonics_are_valid_typed_text_with_no_ctrl_chords_and_no_duplicates()
    {
        foreach (var command in GitGuiPanes.Commands)
        {
            Assert.Null(Mnemonic.Validate(command.Mnemonic));
            Assert.Equal(CommandSource.Local, command.Source);
        }

        Assert.Equal(GitGuiPanes.Commands.Count, GitGuiPanes.Commands.Select(c => c.Mnemonic).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The registry accepts them as local commands, so ids and mnemonics do not clash.
        var result = CommandRegistry.Load(GitGuiPanes.Commands, []);
        Assert.Equal(GitGuiPanes.Commands.Count, result.Registry.Commands.Count);
    }

    [Fact]
    public void Settings_keys_carry_the_documented_defaults()
    {
        var keys = GitGuiPanes.SettingKeys.ToDictionary(s => s.Key, s => s.DefaultValue);

        Assert.Equal(30, keys["git.pr_page_size"]);
        Assert.Equal(3, keys["git.diff_context_lines"]);
        Assert.Equal(30, GitGuiSettings.Default.PrPageSize);
        Assert.Equal(3, GitGuiSettings.Default.DiffContextLines);
    }

    [Fact]
    public void Diff_registry_returns_native_for_monaco_until_one_is_registered()
    {
        var registry = new DiffSurfaceRegistry();

        Assert.Equal(DiffSurfaceKind.Native, registry.Resolve().Kind);
        Assert.Equal(DiffSurfaceKind.Native, registry.Resolve(DiffSurfaceKind.Monaco).Kind);
    }

    [Fact]
    public void Diff_registry_returns_a_registered_monaco_surface()
    {
        var registry = new DiffSurfaceRegistry();
        var monaco = new StubMonaco();
        registry.Register(monaco);

        Assert.Same(monaco, registry.Resolve(DiffSurfaceKind.Monaco));
        Assert.Equal(DiffSurfaceKind.Native, registry.Resolve().Kind);
    }

    private sealed class StubMonaco : IDiffSurface
    {
        public DiffSurfaceKind Kind => DiffSurfaceKind.Monaco;

        public Avalonia.Controls.Control Create(DiffModel model) => new Avalonia.Controls.TextBlock();
    }
}
