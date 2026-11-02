namespace Bazaar.Client.LocalInference.Tests;

public class FakeLocalBackendTests
{
    private static LocalTaskRequest Request(string input = "hello") =>
        new(LocalTaskKind.ChipSummary, input, 64, "key-1");

    [Fact]
    public async Task Returns_scripted_reply_with_the_backend_tier()
    {
        var backend = new FakeLocalBackend(BackendTier.Cpu).Reply("Read 15 files", degraded: true);

        var result = await backend.RunAsync(Request(), CancellationToken.None);

        Assert.Equal("Read 15 files", result.Text);
        Assert.Equal(BackendTier.Cpu, result.Tier);
        Assert.True(result.Degraded);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Scripted_error_is_thrown_then_next_reply_is_used()
    {
        var backend = new FakeLocalBackend().Throw(new TimeoutException("slow")).Reply("ok");

        await Assert.ThrowsAsync<TimeoutException>(() => backend.RunAsync(Request(), CancellationToken.None));
        var result = await backend.RunAsync(Request(), CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal(2, backend.CallCount);
    }

    [Fact]
    public async Task Running_out_of_script_fails_loudly()
    {
        var backend = new FakeLocalBackend();

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.RunAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_token_stops_the_run_without_consuming_a_step()
    {
        var backend = new FakeLocalBackend().Reply("never");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.RunAsync(Request(), cts.Token));
        Assert.Equal(0, backend.CallCount);
    }
}
