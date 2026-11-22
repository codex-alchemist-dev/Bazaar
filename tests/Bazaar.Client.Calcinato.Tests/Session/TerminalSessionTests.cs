#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using System.Text;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Tests.Sources;

namespace Bazaar.Client.Calcinato.Tests.Session;

public sealed class TerminalSessionTests
{
    private static readonly byte[] _deviceAttributesQuery = [0x1B, (byte)'[', (byte)'c'];

    private static TerminalSession Create(FakeTerminalSource source, FakeClock? clock = null) =>
        new("test", source, clock ?? new FakeClock(), 80, 24);

    private static async Task TypeAsync(TerminalSession session, string text)
    {
        foreach (char c in text)
        {
            await session.HandleKeyAsync(TerminalKey.Typed(c), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Output_reaches_the_emulator()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);

        source.Emit("hello");

        await Eventually.TrueAsync(() => session.Emulator.GetLineText(0) == "hello");
        Assert.False(session.IsReplaying);
    }

    [Fact]
    public async Task Device_attributes_reply_is_written_back_when_writable()
    {
        var source = new FakeTerminalSource(canWrite: true);
        await using var session = Create(source);

        source.Emit(_deviceAttributesQuery);

        await Eventually.TrueAsync(() => source.Writes.Count == 1);
        Assert.Equal((byte)0x1B, source.Writes[0][0]);
    }

    [Fact]
    public async Task Device_reply_is_not_written_when_read_only()
    {
        var source = new FakeTerminalSource(canWrite: false);
        await using var session = Create(source);

        source.Emit(_deviceAttributesQuery);
        source.Emit("x");

        await Eventually.TrueAsync(() => session.Emulator.GetLineText(0) == "x");
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task Replayed_output_sets_the_replaying_status_and_its_replies_are_dropped()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);

        source.Emit(Encoding.UTF8.GetBytes("ab").Concat(_deviceAttributesQuery).ToArray(), replay: true);
        await Eventually.TrueAsync(() => session.IsReplaying && session.Emulator.GetLineText(0) == "ab");

        source.Emit("c");
        await Eventually.TrueAsync(() => !session.IsReplaying && session.Emulator.GetLineText(0) == "abc");
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task Insert_mode_text_and_colon_commands_send_bytes_to_the_source()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);

        await TypeAsync(session, "i");
        await TypeAsync(session, "ls");
        await session.HandleKeyAsync(TerminalKey.Named(KeyName.Escape), TestContext.Current.CancellationToken);
        await TypeAsync(session, ":interrupt");
        await session.HandleKeyAsync(TerminalKey.Named(KeyName.Enter), TestContext.Current.CancellationToken);

        // Each key press is its own write; the bytes arrive in order.
        Assert.Equal(Encoding.UTF8.GetBytes("ls").Append((byte)0x03), source.Writes.SelectMany(write => write));
    }

    [Fact]
    public async Task Insert_is_refused_on_a_read_only_source()
    {
        var source = new FakeTerminalSource(canWrite: false);
        await using var session = Create(source);

        await TypeAsync(session, "i");
        await TypeAsync(session, "ls");
        await TypeAsync(session, ":interrupt");
        await session.HandleKeyAsync(TerminalKey.Named(KeyName.Enter), TestContext.Current.CancellationToken);

        // The engine refuses the shell-bound command and keeps the command line open, so the person can cancel it.
        Assert.Equal(ModalMode.CommandLine, session.Engine.Mode);
        Assert.True(session.IsReadOnly);
        Assert.Contains("Read-only", session.StatusText, StringComparison.Ordinal);
        Assert.Empty(source.Writes);

        await session.HandleKeyAsync(TerminalKey.Named(KeyName.Escape), TestContext.Current.CancellationToken);
        Assert.Equal(ModalMode.Normal, session.Engine.Mode);
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task Engine_writability_follows_the_source()
    {
        var source = new FakeTerminalSource(canWrite: true);
        await using var session = Create(source);

        await TypeAsync(session, "i");
        Assert.Equal(ModalMode.Insert, session.Engine.Mode);

        source.CanWrite = false;
        await TypeAsync(session, "x");

        Assert.False(session.Engine.CanWrite);
        Assert.Equal(ModalMode.Normal, session.Engine.Mode);
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task Resize_reaches_the_emulator_and_the_source()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);

        await session.ResizeAsync(100, 30, TestContext.Current.CancellationToken);

        Assert.Equal(100, session.Emulator.Cols);
        Assert.Equal(30, session.Emulator.Rows);
        Assert.Equal((100, 30), Assert.Single(source.Resizes));
    }

    [Fact]
    public async Task Exit_code_is_reported_once_and_the_session_stops_running()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += (_, e) => exited.TrySetResult(e.ExitCode);

        source.Finish(7);

        Assert.Equal(7, await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(7, session.ExitCode);
        Assert.False(session.IsRunningLocalProcess);
        Assert.Equal("Exited with code 7.", session.StatusText);
    }

    [Fact]
    public async Task Running_local_session_reports_it_is_running()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);

        Assert.True(session.IsRunningLocalProcess);
        Assert.False(session.IsRemote);
    }

    [Fact]
    public async Task Remote_session_is_never_a_running_local_process()
    {
        var source = new FakeTerminalSource(isRemote: true);
        await using var session = Create(source);

        Assert.True(session.IsRemote);
        Assert.False(session.IsRunningLocalProcess);
    }

    [Fact]
    public async Task Ex_command_effects_are_raised_as_command_requested()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);
        ExCommand? requested = null;
        session.CommandRequested += (_, command) => requested = command;

        await TypeAsync(session, ":q");
        await session.HandleKeyAsync(TerminalKey.Named(KeyName.Enter), TestContext.Current.CancellationToken);

        Assert.Equal(ExCommandKind.Quit, requested?.Kind);
        Assert.Empty(source.Writes);
    }

    [Fact]
    public async Task Changed_is_raised_for_output()
    {
        var source = new FakeTerminalSource();
        await using var session = Create(source);
        int changes = 0;
        session.Changed += (_, _) => Interlocked.Increment(ref changes);

        source.Emit("a");

        await Eventually.TrueAsync(() => Volatile.Read(ref changes) > 0);
    }

    [Fact]
    public async Task Dispose_disposes_the_source_once_and_rejects_later_input()
    {
        var source = new FakeTerminalSource();
        var session = Create(source);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.True(source.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.HandleKeyAsync(TerminalKey.Typed('i'), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session.ResizeAsync(10, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Output_after_dispose_is_not_processed()
    {
        var source = new FakeTerminalSource();
        var session = Create(source);
        await session.DisposeAsync();

        source.Emit("late");
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, session.Emulator.GetLineText(0));
    }

    [Fact]
    public void Rejects_an_empty_id()
    {
        Assert.Throws<ArgumentException>(() => new TerminalSession(string.Empty, new FakeTerminalSource(), new FakeClock(), 80, 24));
    }
}
