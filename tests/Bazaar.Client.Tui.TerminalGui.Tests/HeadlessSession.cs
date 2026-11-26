using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;

namespace Bazaar.Client.Tui.TerminalGui.Tests;

/// <summary>
/// A Terminal.Gui application on its ANSI driver with no real terminal and a virtual clock, plus a script that feeds keys on the loop
/// thread, one step per loop iteration. When the script runs out the running session is stopped, so a test that scripted too little
/// ends (with a result the test then fails on) instead of hanging.
/// </summary>
internal sealed class HeadlessSession : IDisposable
{
    private readonly Queue<Func<bool>> _steps = new();
    private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(30);
    private int _idle;

    /// <summary>True when the script ran out and the session was stopped for it, so the code under test never ended the run itself.</summary>
    public bool StoppedByScriptEnd { get; private set; }

    public HeadlessSession(int width = 80, int height = 24)
    {
        Host = TerminalGuiHost.Start("ansi", new VirtualTimeProvider());
        Application.Driver!.SetScreenSize(width, height);
        Application.Iteration += OnIteration;
    }

    public TerminalGuiHost Host { get; }

    public IApplication Application => Host.Application;

    /// <summary>Everything the driver would draw right now, as text rows.</summary>
    public string Screen => Application.Driver!.ToString();

    /// <summary>The screen as rows, row 0 first.</summary>
    public string[] Rows => Screen.TrimEnd('\n').Split('\n');

    public HeadlessSession Do(Action step)
    {
        _steps.Enqueue(() =>
        {
            step();
            return true;
        });
        return this;
    }

    /// <summary>Waits (one loop iteration at a time, so the loop keeps running) until <paramref name="ready"/> is true, then runs <paramref name="then"/>.</summary>
    public HeadlessSession Until(Func<bool> ready, Action then)
    {
        _steps.Enqueue(() =>
        {
            if (!ready())
            {
                return false;
            }

            then();
            return true;
        });
        return this;
    }

    public HeadlessSession Press(params Key[] keys)
    {
        foreach (var key in keys)
        {
            Do(() => Application.InjectKey(key));
            Do(() => { });
            Do(() => { });
        }

        return this;
    }

    public HeadlessSession Type(string text)
    {
        foreach (var letter in text)
        {
            var key = new Key(letter);
            Do(() => Application.InjectKey(key));
            Do(() => { });
            Do(() => { });
        }

        return this;
    }

    public HeadlessSession Capture(List<string> screens)
    {
        return Do(() =>
        {
            Application.TopRunnableView?.SetNeedsDraw();
            Application.LayoutAndDraw(true);
            screens.Add(Screen);
        });
    }

    private void OnIteration(object? sender, EventArgs<IApplication?> e)
    {
        if (DateTime.UtcNow > _deadline)
        {
            // A step that waits for something that never comes must not hang the test run.
            StoppedByScriptEnd = true;
            Application.RequestStop();
        }
        else if (_steps.Count > 0)
        {
            if (_steps.Peek()())
            {
                _steps.Dequeue();
            }

            _idle = 0;
        }
        else if (++_idle > 3)
        {
            StoppedByScriptEnd = true;
            Application.RequestStop();
        }
    }

    public void Dispose()
    {
        Application.Iteration -= OnIteration;
        Host.Dispose();
    }
}
