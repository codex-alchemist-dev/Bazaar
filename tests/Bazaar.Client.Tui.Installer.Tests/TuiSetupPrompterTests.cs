using Bazaar.Client.Tui.Tests;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer.Tests;

public sealed class TuiSetupPrompterTests
{
    private const string _canary = "CANARY-s3cret-9f2c";

    private static TuiSetupPrompter Prompter(ScriptedTerminal terminal, bool color = false) =>
        new(new TerminalPromptWidgets(terminal), terminal, new TuiStyle(color));

    private static WizardQuestion Question(WizardQuestionKind kind, string? @default = null, params string[] options) => new()
    {
        Id = "q",
        Prompt = "Question",
        Kind = kind,
        Default = @default,
        Options = options,
    };

    [Fact]
    public void Each_ask_maps_to_its_widget_and_returns_the_answer()
    {
        var terminal = new ScriptedTerminal().Lines("text", "b", "1,2", "yes");
        var prompter = Prompter(terminal);

        Assert.Equal("text", prompter.AskText(Question(WizardQuestionKind.Text)));
        Assert.Equal("b", prompter.ChooseOne(Question(WizardQuestionKind.Choice, "a", "a", "b")));
        Assert.Equal(["a", "b"], prompter.ChooseMany(Question(WizardQuestionKind.MultiChoice, null, "a", "b")));
        Assert.True(prompter.Confirm(Question(WizardQuestionKind.Confirm, "no")));
    }

    [Fact]
    public void Empty_entries_take_the_question_defaults()
    {
        var terminal = new ScriptedTerminal().Lines("", "", "");
        var prompter = Prompter(terminal);

        Assert.Equal("dflt", prompter.AskText(Question(WizardQuestionKind.Text, "dflt")));
        Assert.Equal("b", prompter.ChooseOne(Question(WizardQuestionKind.Choice, "b", "a", "b")));
        Assert.False(prompter.Confirm(Question(WizardQuestionKind.Confirm, "no")));
    }

    [Fact]
    public void Ended_input_returns_null_from_every_ask()
    {
        var prompter = Prompter(new ScriptedTerminal());

        Assert.Null(prompter.AskText(Question(WizardQuestionKind.Text)));
        Assert.Null(prompter.ChooseOne(Question(WizardQuestionKind.Choice, null, "a")));
        Assert.Null(prompter.ChooseMany(Question(WizardQuestionKind.MultiChoice, null, "a")));
        Assert.Null(prompter.Confirm(Question(WizardQuestionKind.Confirm)));
        Assert.Null(prompter.AskSecret(Question(WizardQuestionKind.Secret)));
    }

    [Fact]
    public void Warnings_and_errors_carry_a_word_so_colour_is_never_the_only_signal()
    {
        var terminal = new ScriptedTerminal();
        var prompter = Prompter(terminal, color: false);

        prompter.ShowMessage("be careful", SetupMessageKind.Warning);
        prompter.ShowMessage("bad value", SetupMessageKind.Error);
        prompter.ShowMessage("hello");

        Assert.Equal("Warning: be careful\nError: bad value\nhello\n", terminal.Output);
    }

    [Fact]
    public void Colour_is_added_around_the_label_only_when_enabled()
    {
        var terminal = new ScriptedTerminal();

        Prompter(terminal, color: true).ShowMessage("bad value", SetupMessageKind.Error);

        Assert.Contains("\u001b[", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("Error: bad value", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_draws_a_status_line_for_question_steps_and_stays_quiet_for_review()
    {
        var terminal = new ScriptedTerminal();
        var prompter = Prompter(terminal);

        prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Questions, 2, 4, "channel", "Release channel"));
        prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Review, 4, 4, null, "Review"));

        Assert.Contains("Step 2 of 4: Release channel", terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Review", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_secret_reaches_the_caller_but_never_the_screen()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Typed(_canary);
        var prompter = Prompter(terminal);

        using var secret = prompter.AskSecret(Question(WizardQuestionKind.Secret));

        Assert.NotNull(secret);
        Assert.Equal(_canary, secret.Reveal());
        Assert.DoesNotContain(_canary, terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, secret.ToString(), StringComparison.Ordinal);
    }
}
