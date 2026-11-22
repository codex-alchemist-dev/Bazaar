using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.Setup;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class ServerIdentityCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-identity-check-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Server(string address, string pin) : IServerIdentitySource
    {
        public Uri Address { get; } = new(address);

        public string KeyHashPin { get; } = pin;
    }

    private CompanyBuildOutputLayout Layout() => new(_root, "acme");

    private void RecordBuild(bool complete, string? address, string? pin)
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.CompanyDirectory);
        var addressJson = address is null ? "null" : $"\"{address}\"";
        var pinJson = pin is null ? "null" : $"\"{pin}\"";
        File.WriteAllText(
            layout.HistoryPath,
            $"{{\"companyId\":\"acme\",\"finishedAt\":\"1970-01-01T00:00:00+00:00\",\"complete\":{(complete ? "true" : "false")},\"isSigned\":false,\"targets\":[],\"address\":{addressJson},\"keyHashPin\":{pinJson}}}\n");
    }

    private Task<SetupCheckResult> Run(string address = "https://company.example/", string? pin = null) =>
        new ServerIdentityCheck(Layout(), new Server(address, pin ?? new string('b', 64)))
            .RunAsync(new SetupVerificationContext(new SetupPlan()), TestContext.Current.CancellationToken);

    [Fact]
    public void The_check_replaces_the_server_identity_placeholder() =>
        Assert.Equal("server.identity", new ServerIdentityCheck(Layout(), new Server("https://company.example/", new string('b', 64))).Id);

    [Fact]
    public async Task A_matching_complete_build_is_green()
    {
        RecordBuild(complete: true, "https://company.example/", new string('b', 64));

        Assert.Equal(SetupCheckStatus.Green, (await Run()).Status);
    }

    [Fact]
    public async Task A_moved_server_is_red_with_rebuild_advice()
    {
        RecordBuild(complete: true, "https://old.example/", new string('b', 64));

        var result = await Run();

        Assert.Equal(SetupCheckStatus.Red, result.Status);
        Assert.Contains("Rebuild", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_key_is_red()
    {
        RecordBuild(complete: true, "https://company.example/", new string('c', 64));

        Assert.Equal(SetupCheckStatus.Red, (await Run()).Status);
    }

    [Fact]
    public async Task No_build_yet_and_an_incomplete_build_are_amber_not_green()
    {
        Assert.Equal(SetupCheckStatus.Amber, (await Run()).Status);

        RecordBuild(complete: false, "https://company.example/", new string('b', 64));
        Assert.Equal(SetupCheckStatus.Amber, (await Run()).Status);
    }
}
