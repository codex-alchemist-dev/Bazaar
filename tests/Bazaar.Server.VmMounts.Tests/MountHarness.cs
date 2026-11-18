using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;
using Bazaar.Server.VmMounts.Profiles;
using Bazaar.Server.VmMounts.Tests.Providers;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>A temp directory tree, one agent with one Linux VM, and a manager wired to fakes. Disposing removes the tree.</summary>
public sealed class MountHarness : IDisposable
{
    public const string LinuxProfile = "/var/lib/bazaar/profile";

    public MountHarness(
        bool caseInsensitive = false,
        IReadOnlyList<string>? denyRelativeToAgentsRoot = null,
        int maxScanEntries = 10_000,
        ProfileLimits? limits = null,
        TimeSpan? guestIoTimeout = null,
        GuestMountLayout? windowsLayout = null)
    {
        Root = Directory.CreateTempSubdirectory("vmmounts-").FullName;
        AgentsRoot = Path.Combine(Root, "agents");
        Directory.CreateDirectory(AgentsRoot);
        Outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(Outside);
        Options = new VmMountOptions
        {
            AgentsRoot = AgentsRoot,
            CaseInsensitive = caseInsensitive,
            DenyPaths = [.. (denyRelativeToAgentsRoot ?? []).Select(p => Path.Combine(AgentsRoot, p.Replace("{agent}", Agent.ToString("N"), StringComparison.Ordinal)))],
            MaxScanEntries = maxScanEntries,
            ProfileLimits = limits ?? ProfileLimits.Default,
            GuestIoTimeout = guestIoTimeout ?? TimeSpan.FromMinutes(5),
            Windows = windowsLayout ?? GuestMountLayout.DefaultWindows,
        };
        Policy = new DefaultMountContentPolicy();
        Transfer = new GuestProfileTransfer(Channel, Policy, Options);
        Manager = NewManager();
        CreateAgent(Agent);
        Store.RegisterVm(Vm, Agent);
    }

    public string Root { get; }

    public string AgentsRoot { get; }

    /// <summary>A directory outside the agents root, for escape targets.</summary>
    public string Outside { get; }

    public VmMountOptions Options { get; }

    public Guid Agent { get; } = Guid.CreateVersion7();

    public Guid Vm { get; } = Guid.CreateVersion7();

    public InMemoryScopedMountStore Store { get; } = new();

    public InMemoryCanaryStore Canaries { get; } = new();

    public FakeMountProvider Provider { get; } = new();

    public FakeGuestChannel Channel { get; } = new();

    public RecordingAuditLog Audit { get; } = new();

    public RecordingCanarySink Sink { get; } = new();

    public FakeGuestScrubber Scrubber { get; } = new();

    public FakeHardLinkProbe LinkProbe { get; } = new();

    public DefaultMountContentPolicy Policy { get; }

    public GuestProfileTransfer Transfer { get; }

    public TimeProvider Time { get; } = TimeProvider.System;

    public MountManager Manager { get; }

    /// <summary>A manager over the same stores and fakes. Pass a replacement for any part a test needs to change, or build a second manager to model a restart.</summary>
    public MountManager NewManager(IAuditLog? audit = null, IGuestScrubber? scrubber = null) => new(
        Store, Canaries, Provider, Transfer, Sink, audit ?? Audit, Policy, scrubber ?? Scrubber, LinkProbe, Options, Time);

    /// <summary>Registers another Linux VM for an agent (default: the harness agent) and returns its id.</summary>
    public Guid NewVm(Guid? agent = null, GuestOs os = GuestOs.Linux, bool retired = false)
    {
        var vm = Guid.CreateVersion7();
        Store.RegisterVm(vm, agent ?? Agent, os, retired);
        return vm;
    }

    public string AgentRoot(Guid agent) => Path.Combine(AgentsRoot, agent.ToString("N"));

    public string Soul(Guid agent) => Path.Combine(AgentRoot(agent), "soul");

    public string Workspace(Guid agent) => Path.Combine(AgentRoot(agent), "workspace");

    public string ProfileDirectory(Guid agent) => Path.Combine(Soul(agent), "browser-profile");

    public void CreateAgent(Guid agent)
    {
        Directory.CreateDirectory(Soul(agent));
        Directory.CreateDirectory(Workspace(agent));
    }

    public MountSpec SoulSpec(Guid? vm = null, Guid? agent = null) => new(
        agent ?? Agent, vm ?? Vm, GuestOs.Linux, MountKind.Soul, MountMode.Ro, Soul(agent ?? Agent), Options.Linux.SoulPath);

    public MountSpec WorkspaceSpec(Guid? vm = null, Guid? agent = null, string? hostPath = null) => new(
        agent ?? Agent, vm ?? Vm, GuestOs.Linux, MountKind.Workspace, MountMode.Rw, hostPath ?? Workspace(agent ?? Agent),
        Options.Linux.WorkspacePath);

    /// <summary>Writes a host-side profile file, as an earlier session's write-back would have.</summary>
    public void WriteHostProfile(string relative, string content, Guid? agent = null)
    {
        var path = Path.Combine(ProfileDirectory(agent ?? Agent), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string ReadHostProfile(string relative, Guid? agent = null) =>
        File.ReadAllText(Path.Combine(ProfileDirectory(agent ?? Agent), relative.Replace('/', Path.DirectorySeparatorChar)));

    public bool HostProfileExists(string relative, Guid? agent = null) =>
        File.Exists(Path.Combine(ProfileDirectory(agent ?? Agent), relative.Replace('/', Path.DirectorySeparatorChar)));

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
