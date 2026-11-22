using Terminal.Gui.App;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>
/// The full-screen installer surface (doc 58 work package 3): the question dialogs of <see cref="TerminalGuiPromptWidgets"/> over a
/// scrolling log and progress line, so nothing is written to the console while Terminal.Gui owns it. The wizard runs on a worker
/// task and every widget and sink call is handed to the loop thread (Terminal.Gui views belong to one thread, and the wizard's own
/// awaits may continue on another). <see cref="Widgets"/> and <see cref="Sink"/> are the pair to give the prompter.
/// </summary>
public sealed class InstallerScreen : IDisposable
{
    private readonly IApplication _application;
    private readonly InstallerBackdrop _backdrop;
    private readonly CancellationTokenSource _closed = new();

    internal InstallerScreen(IApplication application, string title)
    {
        _application = application;
        _backdrop = new InstallerBackdrop(application, title);
        var onLoop = new LoopMarshal(application, _closed.Token);
        Widgets = new LoopThreadWidgets(onLoop, new TerminalGuiPromptWidgets(application));
        Sink = new TerminalGuiOutputSink(onLoop, _backdrop);
    }

    public void Dispose()
    {
        _closed.Dispose();
        _backdrop.Dispose();
    }

    /// <summary>Question widgets; callable from any thread, they run on the loop thread and return the answer.</summary>
    public IPromptWidgets Widgets { get; }

    /// <summary>The scrolling log and progress line; callable from any thread.</summary>
    public ITuiOutputSink Sink { get; }

    /// <summary>The log as drawn now, for tests.</summary>
    internal string LogText => _backdrop.LogText;

    internal string ProgressLine => _backdrop.ProgressLine;

    internal string StatusText => _backdrop.StatusText;

    /// <summary>
    /// Runs <paramref name="work"/> on a worker task while this thread runs the screen, and returns its result. With
    /// <paramref name="waitForClose"/> the screen stays up after the work ends, so the operator can read the report, until Enter, Esc
    /// or Ctrl+C. An exception from the work is thrown here once the terminal is restored.
    /// </summary>
    public T Run<T>(Func<Task<T>> work, bool waitForClose = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        Task<T>? task = null;
        void Start(object? sender, EventArgs e)
        {
            _application.Iteration -= Start;
            task = Task.Run(work);
            _ = task.ContinueWith(
                _ => _application.Invoke(() =>
                {
                    if (waitForClose)
                    {
                        _backdrop.Finish("Finished. Press Enter to close.");
                    }
                    else
                    {
                        _application.RequestStop(_backdrop);
                    }
                }),
                TaskScheduler.Default);
        }

        _application.Iteration += Start;
        try
        {
            _application.Run(_backdrop);
        }
        finally
        {
            _application.Iteration -= Start;
            _closed.Cancel();
        }

        // Closed before the work ended (a test's script ran out): the cancelled marshal ends the worker.
        return task is null ? throw new InvalidOperationException("The installer screen closed before the work started.") : task.GetAwaiter().GetResult();
    }
}

/// <summary>Hands a call to the loop thread and waits for it. Ends with <see cref="OperationCanceledException"/> once the screen is closed.</summary>
internal sealed class LoopMarshal(IApplication application, CancellationToken closed)
{
    public T Call<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.Invoke(() =>
        {
            try
            {
                done.TrySetResult(action());
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        });
        try
        {
            done.Task.Wait(closed);
        }
        catch (AggregateException aggregate) when (aggregate.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(aggregate.InnerException).Throw();
        }

        return done.Task.Result;
    }

    /// <summary>Queues a call without waiting. Calls and <see cref="Call{T}"/> run in the order made.</summary>
    public void Post(Action action) => application.Invoke(action);
}

internal sealed class LoopThreadWidgets(LoopMarshal loop, IPromptWidgets inner) : IPromptWidgets
{
    public string? TextEntry(string prompt, string? defaultValue) => loop.Call(() => inner.TextEntry(prompt, defaultValue));

    public string? SecretEntry(string prompt) => loop.Call(() => inner.SecretEntry(prompt));

    public string? PickOne(string prompt, IReadOnlyList<string> options, string? defaultOption) =>
        loop.Call(() => inner.PickOne(prompt, options, defaultOption));

    public IReadOnlyList<string>? PickMany(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults) =>
        loop.Call(() => inner.PickMany(prompt, options, defaults));

    public bool? YesNo(string prompt, bool defaultYes) => loop.Call(() => inner.YesNo(prompt, defaultYes));
}

/// <summary><see cref="ITuiOutputSink"/> on the installer backdrop. Calls are queued to the loop thread in order, so they interleave correctly with the dialogs.</summary>
internal sealed class TerminalGuiOutputSink(LoopMarshal loop, InstallerBackdrop backdrop) : ITuiOutputSink
{
    public void WriteMessage(string line, TuiMessageKind kind = TuiMessageKind.Info)
    {
        ArgumentNullException.ThrowIfNull(line);
        loop.Post(() => backdrop.Append(line, kind));
    }

    public void ShowProgress(TuiProgress progress) => loop.Post(() => backdrop.SetProgress(progress));

    public void WriteReport(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var copy = lines.ToArray();
        loop.Post(() =>
        {
            foreach (var line in copy)
            {
                backdrop.Append(line, TuiMessageKind.Info);
            }
        });
    }
}
