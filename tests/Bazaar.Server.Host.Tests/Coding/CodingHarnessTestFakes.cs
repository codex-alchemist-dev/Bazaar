using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Identity;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

/// <summary>
/// Contract-first fakes for the ports that have no production source yet (<see cref="CodingHarnessPorts.Required"/>). They are
/// test-only: production registers none of them, so an enabled harness with no host binding stops at start.
/// </summary>
public static class CodingHarnessTestFakes
{
    /// <summary>Configuration with <c>Coding:Harness:Enabled</c> set.</summary>
    public static IConfiguration Config(bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Coding:Harness:Enabled", enabled ? "true" : "false"), new KeyValuePair<string, string?>("Coding:Harness:Git:AppNoreplyEmail", "bot@noreply.example.test")])
            .Build();

    /// <summary>
    /// Registers a fake for every required port except <paramref name="except"/>, so a test can leave exactly one unbound.
    /// </summary>
    public static IServiceCollection AddFakes(this IServiceCollection services, Type? except = null)
    {
        if (except != typeof(ICodingRepoDirectory)) services.AddSingleton<ICodingRepoDirectory>(new FakeRepoDirectory());
        if (except != typeof(IWorkShareLayout)) services.AddSingleton<IWorkShareLayout>(new FakeWorkShareLayout());
        if (except != typeof(ISubagentWorktreeAllocator)) services.AddSingleton<ISubagentWorktreeAllocator>(new FakeWorktreeAllocator());
        if (except != typeof(IAgentGitIdentity)) services.AddSingleton<IAgentGitIdentity>(new FakeGitIdentity());
        if (except != typeof(IRuntimeSubagentParentDirectory)) services.AddSingleton<IRuntimeSubagentParentDirectory>(new FakeParentDirectory());
        if (except != typeof(IAgentStateReader)) services.AddSingleton<IAgentStateReader>(new FakeAgentState());
        if (except != typeof(IPushSessionRecords)) services.AddSingleton<IPushSessionRecords>(new FakeSessionRecords());
        return services;
    }

    private sealed class FakeRepoDirectory : ICodingRepoDirectory
    {
        public ValueTask<CodingRepoBinding?> FindAsync(string repoId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<CodingRepoBinding?>(null);
    }

    private sealed class FakeWorkShareLayout : IWorkShareLayout
    {
        public string GetWorktreeRoot(string agentId) => Path.Combine(Path.GetTempPath(), "fake-worktrees", agentId);

        public string GetPassportFolder(string agentId) => Path.Combine(Path.GetTempPath(), "fake-passport", agentId);
    }

    private sealed class FakeWorktreeAllocator : ISubagentWorktreeAllocator
    {
        public Task<WorktreeLease> AllocateAsync(WorktreeAllocation request, CancellationToken cancellationToken) =>
            Task.FromResult(new WorktreeLease("fake-lease", Path.GetTempPath(), request.ReadOnly));

        public Task ReleaseAsync(WorktreeLease lease, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGitIdentity : IAgentGitIdentity
    {
        public ValueTask<GitAuthor> GetAuthorAsync(string agentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GitAuthor("Fake Agent", "fake-agent@example.invalid"));
    }

    private sealed class FakeParentDirectory : IRuntimeSubagentParentDirectory
    {
        public ValueTask<RuntimeSubagentParent?> FindAsync(Guid parentTurnId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RuntimeSubagentParent?>(null);
    }

    private sealed class FakeAgentState : IAgentStateReader
    {
        public Bazaar.Server.Security.Taint.SessionTaint TaintFor(string agentId) => Bazaar.Server.Security.Taint.SessionTaint.Clean;

        public bool IsQuarantined(string agentId) => false;
    }

    private sealed class FakeSessionRecords : IPushSessionRecords
    {
        public ValueTask<PushSessionRecord?> FindAsync(PushGateRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PushSessionRecord?>(null);
    }
}
