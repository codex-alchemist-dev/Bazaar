using System.Collections.Concurrent;
using System.Net;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Guest;
using Bazaar.Server.VmMounts.Providers.Smb;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.VmMounts.Providers.Tests;

/// <summary>A temporary agents root with one agent folder holding the soul and workspace folders.</summary>
internal sealed class MountEnv : IDisposable
{
    public MountEnv(Action<VmMountOptionsBuilder>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-providers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Root = Path.GetFullPath(Root);
        AgentId = Guid.NewGuid();
        AgentRoot = Path.Combine(Root, AgentId.ToString("N"));
        SoulPath = Path.Combine(AgentRoot, "soul");
        WorkspacePath = Path.Combine(AgentRoot, "workspace");
        Directory.CreateDirectory(SoulPath);
        Directory.CreateDirectory(WorkspacePath);

        var builder = new VmMountOptionsBuilder();
        configure?.Invoke(builder);
        Options = new VmMountOptions
        {
            AgentsRoot = Root,
            CaseInsensitive = false,
            DenyPaths = builder.DenyPaths,
            GuestIoTimeout = builder.GuestIoTimeout,
            ProfileLimits = builder.Limits,
        };
    }

    public string Root { get; }

    public Guid AgentId { get; }

    public string AgentRoot { get; }

    public string SoulPath { get; }

    public string WorkspacePath { get; }

    public VmMountOptions Options { get; }

    public MountProviderRequest Request(
        MountKind kind, MountMode mode, string? hostPath = null, Guid? mountId = null, Guid? vmId = null, GuestOs os = GuestOs.Linux)
    {
        var guest = os == GuestOs.Linux
            ? (kind == MountKind.Soul ? "/mnt/bazaar/soul" : "/mnt/bazaar/workspace")
            : (kind == MountKind.Soul ? @"C:\Bazaar\Soul" : @"C:\Bazaar\Workspace");
        return new MountProviderRequest(
            mountId ?? Guid.NewGuid(), AgentId, vmId ?? Guid.NewGuid(), os, kind, mode,
            hostPath ?? (kind == MountKind.Soul ? SoulPath : WorkspacePath), guest);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class VmMountOptionsBuilder
{
    public IReadOnlyList<string> DenyPaths { get; set; } = [];

    public TimeSpan GuestIoTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public ProfileLimits Limits { get; set; } = ProfileLimits.Default;
}

/// <summary>Records every command and answers from a script. Counts overlapping runs.</summary>
internal sealed class FakeRunner : IHostCommandRunner
{
    private readonly ConcurrentQueue<HostCommand> _commands = new();
    private int _running;

    public IReadOnlyList<HostCommand> Commands => [.. _commands];

    public int MaxConcurrent { get; private set; }

    public TimeSpan Delay { get; set; }

    public Func<HostCommand, HostCommandResult> Answer { get; set; } = _ => new HostCommandResult(0, "ok\n", string.Empty);

    public async ValueTask<HostCommandResult> RunAsync(HostCommand command, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _running);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        _commands.Enqueue(command);
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            return Answer(command);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

internal sealed class FakeIdentity(List<string>? log = null) : IVmShareIdentity
{
    public string Account { get; set; } = @"BAZAAR-HOST\bazaar-vm1";

    public int Calls { get; private set; }

    public Task<VmShareAccount> EnsureAccountAsync(Guid agentId, Guid vmId, MountKind kind, CancellationToken ct)
    {
        Calls++;
        log?.Add("identity");
        return Task.FromResult(new VmShareAccount(Account));
    }
}

internal sealed class FakeEndpoint(List<string>? log = null) : IVmShareEndpoint
{
    public IPAddress Address { get; set; } = IPAddress.Parse("172.20.0.1");

    public int Calls { get; private set; }

    public Task<IPAddress> GetHostAddressAsync(Guid agentId, Guid vmId, CancellationToken ct)
    {
        Calls++;
        log?.Add("endpoint");
        return Task.FromResult(Address);
    }
}

internal sealed class FakeAudit : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public IReadOnlyList<AuditEntry> Entries => [.. _entries];

    /// <summary>The number of appends that throw before appends succeed again. Negative: always throw.</summary>
    public int FailNext { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (FailNext != 0)
        {
            if (FailNext > 0)
                FailNext--;
            throw new InvalidOperationException("audit store is down");
        }

        _entries.Enqueue(e);
        return Task.CompletedTask;
    }
}

internal sealed class FakeContainment : IAgentContainment
{
    public List<(string AgentId, string Actor)> Calls { get; } = [];

    /// <summary>The number of calls that throw before calls succeed again. Negative: always throw.</summary>
    public int FailNext { get; set; }

    public bool Hang { get; set; }

    public async Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        Calls.Add((agentId, actorId));
        if (Hang)
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        if (FailNext != 0)
        {
            if (FailNext > 0)
                FailNext--;
            throw new InvalidOperationException("containment leg failed");
        }
    }

    public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeStore : IScopedMountStore
{
    public Dictionary<Guid, GuestOs> Vms { get; } = [];

    public List<ScopedMountRow> Rows { get; } = [];

    public Task<MountInsertResult> TryInsertAsync(ScopedMountRow row, GuestOs guestOs, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GuestOs?> GetVmGuestOsAsync(Guid vmId, CancellationToken ct) =>
        Task.FromResult<GuestOs?>(Vms.TryGetValue(vmId, out var os) ? os : null);

    public Task<IReadOnlyList<ScopedMountRow>> ListAttachedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ScopedMountRow>>([.. Rows.Where(r => r.DetachedAt is null)]);

    public Task<ScopedMountRow?> GetAsync(Guid mountId, CancellationToken ct) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.Id == mountId));

    public Task<IReadOnlyList<ScopedMountRow>> ListAttachedForAgentAsync(Guid agentId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ScopedMountRow>>([.. Rows.Where(r => r.AgentId == agentId && r.DetachedAt is null)]);

    public Task<bool> MarkDetachedAsync(Guid mountId, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeGuestChannel : IGuestChannel
{
    public List<(string Path, string Sha256, byte[] Bytes)> Puts { get; } = [];

    public List<string> Gets { get; } = [];

    public Func<string, Stream, CancellationToken, Task>? OnGet { get; set; }

    public Func<string, Stream, string, CancellationToken, Task>? OnPut { get; set; }

    public async Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct)
    {
        if (OnPut is not null)
        {
            await OnPut(guestPath, content, sha256, ct);
            return;
        }

        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        Puts.Add((guestPath, sha256, copy.ToArray()));
    }

    public async Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct)
    {
        Gets.Add(guestPath);
        if (OnGet is not null)
        {
            await OnGet(guestPath, sink, ct);
            return;
        }

        await sink.WriteAsync("data"u8.ToArray(), ct);
    }

    public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteFileAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GuestDirectoryEntry>> ListFilesAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeChannelProvider(FakeGuestChannel? channel) : IGuestChannelProvider
{
    public ValueTask<IGuestChannel?> TryGetAsync(Guid vmId, CancellationToken ct) => ValueTask.FromResult<IGuestChannel?>(channel);
}

/// <summary>A seekable stream that claims a huge length without holding the bytes.</summary>
internal sealed class HugeStream(long length) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position { get; set; }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A readable stream that is not seekable.</summary>
internal sealed class ForwardOnlyStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
