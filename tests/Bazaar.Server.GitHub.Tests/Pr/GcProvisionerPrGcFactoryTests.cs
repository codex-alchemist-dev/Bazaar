using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.GitHub.Ingress;
using Bazaar.Server.GitHub.Pr;

namespace Bazaar.Server.GitHub.Tests.Pr;

/// <summary>The adapter from <see cref="IGitHubPrGcFactory"/> to <see cref="IGcProvisioner"/>: create with the creator, then add each other member.</summary>
public sealed class GcProvisionerPrGcFactoryTests
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid _agent = Guid.Parse("00000000-0000-0000-0000-00000000a001");

    private sealed class FakeProvisioner : IGcProvisioner
    {
        public List<string> Calls { get; } = [];
        public string? TypeKey { get; private set; }

        public Task<bool> CreateProjectGcAsync(Guid gcId, string title, GcPrincipal owner, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> CreateAsync(Guid gcId, string typeKey, string title, GcPrincipal owner, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
        {
            TypeKey = typeKey;
            Calls.Add($"create:{owner.Id}:{owner.Kind}:{metadata!["number"]}");
            return Task.FromResult(true);
        }

        public Task<Guid> EnsureDmAsync(GcPrincipal first, GcPrincipal second, CancellationToken ct) => throw new NotSupportedException();

        public Task<Guid?> FindAsync(string typeKey, Guid ownerId, string metadataKey, string metadataValue, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> AddMemberAsync(Guid gcId, GcPrincipal member, CancellationToken ct)
        {
            Calls.Add($"add:{member.Id}:{member.Kind}");
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Ensure_creates_with_the_creator_then_adds_the_other_members_once()
    {
        var provisioner = new FakeProvisioner();
        var factory = new GcProvisionerPrGcFactory(provisioner, new GitHubPrGcOptions { OwnerPrincipalId = _owner }, new GitHubIngressOptions());
        var bridge = GitHubBridgePrincipal.Id;

        var created = await factory.EnsureAsync(
            Guid.NewGuid(), "t", _owner, [_owner, bridge, _agent, _agent], new Dictionary<string, string> { ["number"] = "7" }, TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.Equal("pr", provisioner.TypeKey);
        Assert.Equal([$"create:{_owner}:Owner:7", $"add:{bridge}:Agent", $"add:{_agent}:Agent"], provisioner.Calls);
    }

    [Fact]
    public async Task AddAgent_adds_an_agent_member()
    {
        var provisioner = new FakeProvisioner();
        var factory = new GcProvisionerPrGcFactory(provisioner, new GitHubPrGcOptions(), new GitHubIngressOptions());

        Assert.True(await factory.AddAgentAsync(Guid.NewGuid(), _agent, "bob", TestContext.Current.CancellationToken));
        Assert.Equal([$"add:{_agent}:Agent"], provisioner.Calls);
    }
}
