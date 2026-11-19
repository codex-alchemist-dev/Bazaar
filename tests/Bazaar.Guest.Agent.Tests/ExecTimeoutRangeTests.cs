using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

public sealed class ExecTimeoutRangeTests
{
    [Fact]
    public async Task An_exec_timeout_beyond_the_timer_range_does_not_crash_the_agent()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        var argv = OperatingSystem.IsWindows() ? new[] { "cmd", "/c", "exit", "0" } : new[] { "true" };
        await guest.SendAsync(new Exec("big", argv, root.Root, [], int.MaxValue));

        // Whatever the agent decided about the exec, it must still answer the host afterwards.
        await guest.ReceiveExitAsync("big");
        await guest.SyncAsync();
    }
}
