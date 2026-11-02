using Bazaar.Client.LocalInference.Chips;

namespace Bazaar.Client.LocalInference.Tests.Chips;

public class ChipLabelerTests
{
    [Fact]
    public void Labels_read_files_with_a_count()
    {
        var label = ChipLabeler.Label([new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 15, ".cs")]);

        Assert.Equal("Read 15 files (.cs)", label);
    }

    [Fact]
    public void Labels_edited_files_with_their_types()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 2, ".cs"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 1, ".md"),
        ]);

        Assert.Equal("Edited 3 files (.cs, .md)", label);
    }

    [Fact]
    public void Labels_commands_without_types()
    {
        var label = ChipLabeler.Label([new ActionLogEntry(ActionVerb.Ran, ActionTarget.Command, 2)]);

        Assert.Equal("Ran 2 commands", label);
    }

    [Fact]
    public void Uses_singular_nouns_for_a_count_of_one()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 1, ".md"),
            new ActionLogEntry(ActionVerb.Ran, ActionTarget.Command, 1),
        ]);

        Assert.Equal("Read 1 file (.md); Ran 1 command", label);
    }

    [Fact]
    public void Joins_groups_in_verb_order_then_target_order()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Ran, ActionTarget.Command, 2),
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 15, ".cs"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 3, ".cs"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 3, ".md"),
        ]);

        Assert.Equal("Read 15 files (.cs); Edited 6 files (.cs, .md); Ran 2 commands", label);
    }

    [Fact]
    public void Same_input_gives_the_same_label()
    {
        var entries = new[]
        {
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 4, ".json"),
            new ActionLogEntry(ActionVerb.Fetched, ActionTarget.Web, 2),
        };

        Assert.Equal(ChipLabeler.Label(entries), ChipLabeler.Label(entries));
    }

    [Fact]
    public void Ignores_entries_with_a_count_of_zero_or_less()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 0, ".cs"),
            new ActionLogEntry(ActionVerb.Ran, ActionTarget.Command, -1),
            new ActionLogEntry(ActionVerb.Ran, ActionTarget.Command, 2),
        ]);

        Assert.Equal("Ran 2 commands", label);
    }

    [Fact]
    public void No_entries_gives_the_no_actions_label()
    {
        Assert.Equal(ChipLabeler.NoActions, ChipLabeler.Label([]));
    }

    [Fact]
    public void Normalises_extension_case_and_missing_dot()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 1, "CS"),
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 1, ".Cs"),
        ]);

        Assert.Equal("Read 2 files (.cs)", label);
    }

    [Fact]
    public void Blank_or_invalid_extensions_are_left_out()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Created, ActionTarget.File, 2, null),
            new ActionLogEntry(ActionVerb.Created, ActionTarget.File, 1, "  "),
            new ActionLogEntry(ActionVerb.Created, ActionTarget.File, 1, "a/b"),
        ]);

        Assert.Equal("Created 4 files", label);
    }

    [Fact]
    public void Lists_at_most_three_extensions_then_counts_the_rest()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 5, ".cs"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 4, ".md"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 3, ".json"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 2, ".yml"),
            new ActionLogEntry(ActionVerb.Edited, ActionTarget.File, 1, ".txt"),
        ]);

        Assert.Equal("Edited 15 files (.cs, .md, .json, +2 more)", label);
    }

    [Fact]
    public void Orders_extensions_by_count_then_name()
    {
        var label = ChipLabeler.Label(
        [
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 1, ".md"),
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 1, ".cs"),
            new ActionLogEntry(ActionVerb.Read, ActionTarget.File, 3, ".json"),
        ]);

        Assert.Equal("Read 5 files (.json, .cs, .md)", label);
    }

    [Fact]
    public void Web_pages_use_page_nouns()
    {
        var label = ChipLabeler.Label([new ActionLogEntry(ActionVerb.Fetched, ActionTarget.Web, 3, ".html")]);

        Assert.Equal("Fetched 3 pages", label);
    }
}
