using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Tests.Sources;

public sealed class TerminalHostTests
{
    private static readonly TerminalSpec Spec = new("bash", 80, 24);

    [Fact]
    public void Host_without_a_backend_has_no_local_terminals()
    {
        var host = new TerminalHost(null, new FakeHub(new FakeWire()));

        Assert.False(host.SupportsLocal);
        Assert.Throws<NotSupportedException>(() => host.OpenLocal(Spec));
    }

    [Fact]
    public void Host_with_an_unsupported_backend_has_no_local_terminals()
    {
        var backend = new FakePtyBackend(isSupported: false);
        var host = new TerminalHost(backend, new FakeHub(new FakeWire()));

        Assert.False(host.SupportsLocal);
        Assert.Throws<NotSupportedException>(() => host.OpenLocal(Spec));
        Assert.Empty(backend.Opened);
    }

    [Fact]
    public async Task Host_with_a_supported_backend_opens_a_local_source()
    {
        var backend = new FakePtyBackend(isSupported: true);
        var host = new TerminalHost(backend, new FakeHub(new FakeWire()));

        Assert.True(host.SupportsLocal);
        await using var source = host.OpenLocal(Spec);

        Assert.False(source.IsRemote);
        Assert.True(source.CanWrite);
        Assert.Equal(Spec, Assert.Single(backend.Opened));
    }

    [Fact]
    public async Task Attach_asks_the_hub_for_the_terminal_and_returns_an_opened_remote_source()
    {
        var wire = new FakeWire();
        var hub = new FakeHub(wire);
        var host = new TerminalHost(null, hub);

        var attach = host.AttachRemoteAsync("t9", 90, 30, TestContext.Current.CancellationToken);
        wire.Push(new TerminalOpened("t9", WriterHeld: false, ReplayFrom: 1));
        await using var source = await attach;

        Assert.Equal("t9", Assert.Single(hub.Requested));
        Assert.True(source.IsRemote);
        Assert.True(source.CanWrite);
        var open = Assert.IsType<TerminalOpen>(Assert.Single(wire.Sent));
        Assert.Equal(("t9", 90, 30), (open.TerminalId, open.Cols, open.Rows));
    }

    [Fact]
    public async Task Attach_is_refused_by_the_server_and_the_source_is_released()
    {
        var wire = new FakeWire();
        var host = new TerminalHost(null, new FakeHub(wire));

        var attach = host.AttachRemoteAsync("t9", 80, 24, TestContext.Current.CancellationToken);
        wire.Push(new TerminalError("t9", "forbidden", "no access"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => attach);
        Assert.Single(wire.SentOf<TerminalClose>());
    }

    [Fact]
    public void Host_rejects_a_null_hub()
    {
        Assert.Throws<ArgumentNullException>(() => new TerminalHost(null, null!));
    }
}
