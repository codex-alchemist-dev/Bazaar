using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Bazaar.Client.Tui.TerminalGui.Tests;

public class InstallerScreenTests
{
    private static bool DialogUp(HeadlessSession session) => session.Application.TopRunnableView is Dialog;

    private static bool Finished(InstallerScreen screen) => screen.StatusText.Contains("Finished", StringComparison.Ordinal);

    [Fact]
    public void Messages_progress_and_report_are_drawn_on_the_screen_in_order_and_the_work_result_comes_back()
    {
        using var session = new HeadlessSession();
        using var screen = session.Host.CreateInstallerScreen("Test installer");
        var screens = new List<string>();
        session.Until(() => Finished(screen), () => { }).Capture(screens).Press(Key.Enter);

        var result = screen.Run(() =>
        {
            screen.Sink.WriteMessage("first line");
            screen.Sink.ShowProgress(new TuiProgress(2, 4, "Custody"));
            screen.Sink.WriteMessage("be careful", TuiMessageKind.Warning);
            screen.Sink.WriteMessage("it broke", TuiMessageKind.Error);
            screen.Sink.WriteReport(["Result: ok", "Writes: none."]);
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        var drawn = screens[0];
        Assert.Contains("Test installer", drawn);
        Assert.Contains("first line", drawn);
        Assert.Contains("[############------------] Step 2 of 4: Custody", drawn);
        Assert.Contains("Warning: be careful", drawn);
        Assert.Contains("Error: it broke", drawn);
        Assert.True(drawn.IndexOf("Result: ok", StringComparison.Ordinal) < drawn.IndexOf("Writes: none.", StringComparison.Ordinal));
        Assert.Contains("Finished. Press Enter to close.", drawn);
        Assert.False(session.StoppedByScriptEnd);
    }

    [Fact]
    public void A_question_dialog_opens_over_the_log_and_its_answer_reaches_the_worker()
    {
        using var session = new HeadlessSession();
        using var screen = session.Host.CreateInstallerScreen();
        var screens = new List<string>();
        session
            .Until(() => DialogUp(session), () => { })
            .Type("Acme")
            .Capture(screens)
            .Press(Key.Enter)
            .Until(() => Finished(screen), () => { })
            .Press(Key.Enter);

        var answer = screen.Run(
            () =>
            {
                screen.Sink.WriteMessage("Before the question.");
                var text = screen.Widgets.TextEntry("Company name", null);
                screen.Sink.WriteMessage("Got " + text);
                return Task.FromResult(text);
            });

        Assert.Equal("Acme", answer);
        Assert.Contains("Company name", screens[0]);
        Assert.Contains("Before the question.", screens[0]);
        Assert.Contains("Got Acme", screen.LogText);
        Assert.False(session.StoppedByScriptEnd);
    }

    [Fact]
    public void A_secret_is_masked_in_the_dialog_and_never_reaches_the_log_or_the_screen()
    {
        const string canary = "hunter2-canary";
        using var session = new HeadlessSession();
        using var screen = session.Host.CreateInstallerScreen();
        var screens = new List<string>();
        session
            .Until(() => DialogUp(session), () => { })
            .Type(canary)
            .Capture(screens)
            .Press(Key.Enter)
            .Until(() => Finished(screen), () => { })
            .Capture(screens)
            .Press(Key.Enter);

        var secret = screen.Run(() => Task.FromResult(screen.Widgets.SecretEntry("Passphrase")));

        Assert.Equal(canary, secret);
        Assert.All(screens, drawn => Assert.DoesNotContain(canary, drawn));
        Assert.DoesNotContain(canary, screen.LogText);
        Assert.DoesNotContain(canary, screen.ProgressLine);
    }

    [Fact]
    public void Cancelling_a_question_gives_null_to_the_worker()
    {
        using var session = new HeadlessSession();
        using var screen = session.Host.CreateInstallerScreen();
        session
            .Until(() => DialogUp(session), () => { })
            .Press(Key.C.WithCtrl)
            .Until(() => Finished(screen), () => { })
            .Press(Key.Esc);

        var answer = screen.Run(() => Task.FromResult(screen.Widgets.YesNo("Apply?", defaultYes: true)));

        Assert.Null(answer);
        Assert.False(session.StoppedByScriptEnd);
    }

    [Fact]
    public void An_exception_from_the_work_is_thrown_to_the_caller_after_the_screen_closes()
    {
        using var session = new HeadlessSession();
        using var screen = session.Host.CreateInstallerScreen();
        session.Until(() => Finished(screen), () => { }).Press(Key.Enter);

        Assert.Throws<InvalidOperationException>(() =>
            screen.Run<int>(() => Task.FromException<int>(new InvalidOperationException("boom"))));
    }

    [Fact]
    public void Without_wait_for_close_the_screen_ends_when_the_work_does()
    {
        using var session = new HeadlessSession();
        using var screen = session.Host.CreateInstallerScreen();

        var result = screen.Run(() => Task.FromResult("done"), waitForClose: false);

        Assert.Equal("done", result);
        Assert.False(session.StoppedByScriptEnd);
    }

    [Fact]
    public void A_long_log_keeps_the_newest_lines_and_fits_80_by_24()
    {
        using var session = new HeadlessSession(80, 24);
        using var screen = session.Host.CreateInstallerScreen();
        var screens = new List<string>();
        session.Until(() => Finished(screen), () => { }).Capture(screens).Press(Key.Enter);

        screen.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                screen.Sink.WriteMessage("line number " + i);
            }

            return Task.FromResult(0);
        });

        var rows = screens[0].TrimEnd('\n').Split('\n');
        Assert.Equal(24, rows.Length);
        Assert.Contains("line number 99", screens[0]);
        Assert.DoesNotContain("line number 0\n", screens[0]);
        Assert.Contains("Finished", rows[23]);
    }
}
