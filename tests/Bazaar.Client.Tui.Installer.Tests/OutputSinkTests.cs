using Bazaar.Client.Tui.Tests;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer.Tests;

public sealed class OutputSinkTests
{
    [Fact]
    public void The_console_sink_writes_kinds_as_words_a_progress_bar_and_report_lines()
    {
        var terminal = new ScriptedTerminal();
        var sink = new ConsoleOutputSink(terminal, new TuiStyle(useColor: false));

        sink.WriteMessage("plain");
        sink.WriteMessage("careful", TuiMessageKind.Warning);
        sink.WriteMessage("broken", TuiMessageKind.Error);
        sink.ShowProgress(new TuiProgress(1, 2, "Custody"));
        sink.WriteReport(["Result: valid", "Writes: none."]);

        Assert.Equal(
            "plain\nWarning: careful\nError: broken\n\n[##########----------]" + " Step 1 of 2: Custody\nResult: valid\nWrites: none.\n",
            terminal.Output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_console_sink_ignores_progress_with_no_total()
    {
        var terminal = new ScriptedTerminal();

        new ConsoleOutputSink(terminal, TuiStyle.Plain).ShowProgress(new TuiProgress(0, 0, "x"));

        Assert.Equal(string.Empty, terminal.Output);
    }

    [Fact]
    public void The_prompter_sends_messages_and_only_question_and_apply_progress_to_its_sink()
    {
        var sink = new RecordingSink();
        var prompter = new TuiSetupPrompter(new TerminalPromptWidgets(new ScriptedTerminal()), sink);

        prompter.ShowMessage("hello");
        prompter.ShowMessage("bad", SetupMessageKind.Error);
        prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Questions, 3, 8, "custody", "Custody"));
        prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Review, 8, 8, null, "Review"));

        Assert.Equal(["hello|Info", "bad|Error", "progress 3/8 Custody"], sink.Events);
    }

    private sealed class RecordingSink : ITuiOutputSink
    {
        public List<string> Events { get; } = [];

        public void WriteMessage(string line, TuiMessageKind kind = TuiMessageKind.Info) => Events.Add($"{line}|{kind}");

        public void ShowProgress(TuiProgress progress) => Events.Add($"progress {progress.Current}/{progress.Total} {progress.Title}");

        public void WriteReport(IReadOnlyList<string> lines) => Events.Add("report " + lines.Count);
    }
}
