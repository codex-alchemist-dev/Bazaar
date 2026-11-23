using System.Runtime.InteropServices;

namespace Bazaar.Client.Athanor.WebView2.Tests;

/// <summary>
/// Runs an async test body on a dedicated STA thread that owns a visible top-level window and a Win32 message loop, which
/// WebView2 needs. A <see cref="SynchronizationContext"/> posts continuations back to that loop. No Avalonia, no WinForms.
/// </summary>
internal static partial class Win32Pump
{
    private const uint _wmApp = 0x8000;
    private const int _wsOverlappedWindow = 0x00CF0000;

    private static readonly Lazy<PumpContext> _shared = new(StartThread);

    /// <summary>
    /// Runs <paramref name="body"/> on the one shared STA thread, with a fresh visible top-level window for it. One thread for
    /// the whole test run, like a client's single UI thread: a WebView2 environment belongs to the apartment that made it.
    /// </summary>
    public static Task RunAsync(Func<nint, Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shared.Value.Post(
            async _ =>
            {
                nint window = CreateWindowExW(0, "Static", "athanor-test", _wsOverlappedWindow, 20, 20, 1100, 750, 0, 0, 0, 0);
                ShowWindow(window, 4); // SW_SHOWNOACTIVATE: never steal the foreground from whoever is typing
                try
                {
                    await body(window);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
                finally
                {
                    DestroyWindow(window);
                }
            },
            null);
        return done.Task;
    }

    private static PumpContext StartThread()
    {
        var started = new TaskCompletionSource<PumpContext>();
        var thread = new Thread(() =>
        {
            uint threadId = GetCurrentThreadId();
            var queue = new Queue<(SendOrPostCallback, object?)>();
            var context = new PumpContext(queue, threadId);
            SynchronizationContext.SetSynchronizationContext(context);
            PeekMessageW(out _, 0, 0, 0, 0); // creates the thread's message queue before anyone posts to it
            started.SetResult(context);
            while (GetMessageW(out Msg msg, 0, 0, 0) > 0)
            {
                if (msg.Window == 0 && msg.Message == _wmApp)
                {
                    DrainQueue(queue);
                    continue;
                }

                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        })
        {
            IsBackground = true,
            Name = "webview2-test-sta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task.GetAwaiter().GetResult();
    }

    private static void DrainQueue(Queue<(SendOrPostCallback, object?)> queue)
    {
        while (true)
        {
            (SendOrPostCallback callback, object? state) item;
            lock (queue)
            {
                if (queue.Count == 0)
                {
                    return;
                }

                item = queue.Dequeue();
            }

            item.callback(item.state);
        }
    }

    private sealed class PumpContext(Queue<(SendOrPostCallback, object?)> queue, uint threadId) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (queue)
            {
                queue.Enqueue((d, state));
            }

            PostThreadMessageW(threadId, _wmApp, 0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg msg, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg msg, nint window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref Msg msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
