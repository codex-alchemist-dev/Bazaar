using System.Threading.Channels;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.Terminal.Vms.Tests;

/// <summary>A guest terminal that records what the host sends and lets a test play the guest's output and exit.</summary>
internal sealed class FakeTransportPty : VmTransport.IGuestPty
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public ChannelReader<byte[]> Output => _output.Reader;

    public Task<int> Exited => _exit.Task;

    public List<byte[]> Writes { get; } = [];

    public List<(int Cols, int Rows)> Resizes { get; } = [];

    public int CloseCount { get; private set; }

    public void Emit(string text) => _output.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));

    public void Exit(int code)
    {
        _output.Writer.TryComplete();
        _exit.TrySetResult(code);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Writes.Add(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        Resizes.Add((cols, rows));
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync() => DisposeAsync();

    public ValueTask DisposeAsync()
    {
        CloseCount++;
        _output.Writer.TryComplete();
        _exit.TrySetResult(-1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A guest channel that only opens terminals. Every other call is unused here and throws.</summary>
internal sealed class FakeGuestChannel : IGuestChannel, IGuestChannelLifetime
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen { get; private set; } = true;

    public Task Completion => _completion.Task;

    public List<FakeTransportPty> Ptys { get; } = [];

    public List<(int Cols, int Rows)> Opened { get; } = [];

    public bool FailOpen { get; set; }

    public void End()
    {
        IsOpen = false;
        foreach (var pty in Ptys)
        {
            pty.Exit(-1);
        }

        _completion.TrySetResult();
    }

    public Task<VmTransport.IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct)
    {
        if (FailOpen)
        {
            throw new GuestChannelException("channel ended");
        }

        var pty = new FakeTransportPty();
        Ptys.Add(pty);
        Opened.Add((cols, rows));
        return Task.FromResult<VmTransport.IGuestPty>(pty);
    }

    public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct) => throw new NotSupportedException();

    public Task PutFileAsync(string guestPath, System.IO.Stream content, string sha256, CancellationToken ct) => throw new NotSupportedException();

    public Task GetFileAsync(string guestPath, System.IO.Stream sink, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteFileAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<GuestDirectoryEntry>> ListFilesAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>The VM store port over a list.</summary>
internal sealed class FakeVmLookup : IAgentVmLookup
{
    public List<(Guid AgentId, AgentVmInfo Vm)> Vms { get; } = [];

    public ValueTask<IReadOnlyList<AgentVmInfo>> ListAsync(Guid agentId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AgentVmInfo>>(Vms.Where(v => v.AgentId == agentId).Select(v => v.Vm).ToList());
}

/// <summary>
/// The real registry and directory over a fake VM store, so the adapter is tested against the directory's own ownership and liveness
/// checks and not against a stub of them.
/// </summary>
internal sealed class Rig
{
    public static readonly Guid AgentA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid AgentB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public FakeVmLookup Store { get; } = new();

    public GuestChannelRegistry Registry { get; } = new();

    public GuestChannelDirectory Directory { get; }

    public Rig()
    {
        Directory = new GuestChannelDirectory(Registry, Store);
    }

    /// <summary>Records a live VM of the agent and registers an open channel for it.</summary>
    public (Guid VmId, FakeGuestChannel Channel) AddRunningVm(Guid agent, string name, GuestOs os = GuestOs.Linux)
    {
        var vmId = Guid.NewGuid();
        Store.Vms.Add((agent, new AgentVmInfo(vmId, os, name, Retired: false)));
        var channel = new FakeGuestChannel();
        Registry.Register(vmId, agent.ToString("D"), name, os, channel);
        return (vmId, channel);
    }
}
