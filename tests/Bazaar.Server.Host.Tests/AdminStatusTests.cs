using Bazaar.Contracts;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Providers;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Host.Tests;

public sealed class AdminStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-host-status-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_status_port_reports_the_same_facts_as_the_status_listener()
    {
        var state = new HostState(TimeProvider.System);
        var source = new HostAdminStatusSource(state, new ProviderCapabilityReport([]));

        var view = await source.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProductVersion.Informational, view.Version);
        Assert.Equal(ProductVersion.CurrentCodename, view.Codename);
        Assert.Equal(state.StartedAt, view.StartedAt);
        Assert.Empty(view.ProviderLines);
    }

    [Fact]
    public async Task The_file_service_writes_a_file_the_tui_side_reader_accepts_and_survives_an_unwritable_path()
    {
        var state = new HostState(TimeProvider.System);
        var source = new HostAdminStatusSource(state, new ProviderCapabilityReport([]));
        var path = Path.Combine(_dir, "ops", AdminStatusFile.FileName);
        var service = new AdminStatusFileService(source, path, TimeProvider.System, NullLogger<AdminStatusFileService>.Instance);

        Assert.False(await service.WriteOnceAsync(false, TestContext.Current.CancellationToken));
        var view = AdminStatusFile.Read(path, DateTimeOffset.UtcNow, AdminStatusFile.DefaultStaleAfter);
        Assert.Equal(ProductVersion.Informational, view.Version);

        var blocker = Path.Combine(_dir, "blocker");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        var broken = new AdminStatusFileService(source, Path.Combine(blocker, "s.json"), TimeProvider.System, NullLogger<AdminStatusFileService>.Instance);
        Assert.True(await broken.WriteOnceAsync(false, TestContext.Current.CancellationToken));
        Assert.True(await broken.WriteOnceAsync(true, TestContext.Current.CancellationToken));
    }
}
