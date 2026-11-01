using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

public sealed class PtyTests
{
    [Fact]
    public async Task Pty_open_starts_the_shell_with_the_requested_size()
    {
        await using var root = new TempRoot();
        var pty = new FakePtyHost();
        var options = root.Options() with { ShellPath = "/bin/test-shell" };
        await using var guest = new GuestHarness(options, pty);
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new PtyOpen("pty-1", 120, 40));
        await pty.Opened.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Equal("/bin/test-shell", pty.ShellPath);
        Assert.Equal(120, pty.Cols);
        Assert.Equal(40, pty.Rows);
    }

    [Fact]
    public async Task Data_and_resize_reach_the_terminal_and_close_ends_it()
    {
        await using var root = new TempRoot();
        var pty = new FakePtyHost();
        await using var guest = new GuestHarness(root.Options(), pty);
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new PtyOpen("pty-1", 80, 24));
        var session = await pty.Opened.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        await guest.SendAsync(new PtyData("pty-1", 0, "ls\n"u8.ToArray()));
        await guest.SendAsync(new PtyResize("pty-1", 200, 60));
        await guest.SendAsync(new PtyClose("pty-1"));

        await session.Closed.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        Assert.Equal("ls\n"u8.ToArray(), Assert.Single(session.Input));
        Assert.Equal((200, 60), Assert.Single(session.Resizes));
    }

    [Fact]
    public async Task Terminal_output_returns_as_pty_data_with_the_agents_own_sequence()
    {
        await using var root = new TempRoot();
        var pty = new FakePtyHost();
        await using var guest = new GuestHarness(root.Options(), pty);
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new PtyOpen("pty-2", 80, 24));
        var session = await pty.Opened.WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        await session.EmitAsync("one"u8.ToArray());
        await session.EmitAsync("two"u8.ToArray());

        var first = await guest.ReceiveAsync<PtyData>();
        var second = await guest.ReceiveAsync<PtyData>();
        Assert.Equal("pty-2", first.Id);
        Assert.Equal(0, first.Seq);
        Assert.Equal("one"u8.ToArray(), first.Bytes);
        Assert.Equal(1, second.Seq);
        Assert.Equal("two"u8.ToArray(), second.Bytes);
    }

    [Fact]
    public async Task Pty_data_for_an_unknown_terminal_is_ignored()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options(), new FakePtyHost());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new PtyData("no-such-pty", 0, "x"u8.ToArray()));

        // Nothing is sent back for it, so the next frame is the heartbeat echo.
        await guest.SyncAsync();
    }
}
