using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Sources;

namespace Bazaar.Client.Calcinato.Tests.Sources;

public sealed class LocalTerminalSourceTests
{
    [Fact]
    public async Task Output_is_forwarded_with_sequence_zero_and_source_is_writable()
    {
        var session = new FakePtySession();
        await using var source = new LocalTerminalSource(session);

        session.Emit([1, 2, 3]);
        var chunk = await Eventually.NextAsync(source.Output);

        Assert.False(source.IsRemote);
        Assert.True(source.CanWrite);
        Assert.Equal(0, chunk.Seq);
        Assert.False(chunk.IsReplay);
        Assert.Equal(new byte[] { 1, 2, 3 }, chunk.Bytes.ToArray());
    }

    [Fact]
    public async Task Write_and_resize_reach_the_session()
    {
        var session = new FakePtySession();
        await using var source = new LocalTerminalSource(session);

        await source.WriteAsync(new byte[] { 0x61 }, TestContext.Current.CancellationToken);
        await source.ResizeAsync(100, 30, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x61 }, Assert.Single(session.Writes));
        Assert.Equal((100, 30), Assert.Single(session.Resizes));
    }

    [Fact]
    public async Task Exit_code_passes_through_and_output_completes()
    {
        var session = new FakePtySession();
        await using var source = new LocalTerminalSource(session);

        session.Finish(7);

        Assert.Equal(7, await source.Exited);
        await foreach (var _ in source.Output.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.Fail("No output expected after exit.");
        }
    }

    [Fact]
    public async Task Dispose_ends_the_session_and_completes_output()
    {
        var session = new FakePtySession();
        var source = new LocalTerminalSource(session);

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.True(session.Disposed);
        Assert.Equal(-1, await source.Exited);
        await foreach (var _ in source.Output.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.Fail("Output must complete after dispose.");
        }
    }
}
