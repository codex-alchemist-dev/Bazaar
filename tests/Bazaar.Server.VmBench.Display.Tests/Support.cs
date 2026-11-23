using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmBench.Display.Tests;

internal static class Bench
{
    public const double Mib = 1024d * 1024d;

    public static DisplayBenchCriteria Criteria(Action<DisplayBenchCriteria>? tweak = null)
    {
        var c = new DisplayBenchCriteria { SettleSeconds = 0, ScreenshotSamples = 5, InputSamples = 5 };
        tweak?.Invoke(c);
        return c;
    }

    public static DisplayBenchContext Context(FakeTimeProvider time, DisplayBenchCriteria? c = null) =>
        new(DisplayBenchCriteriaSnapshot.From(c ?? Criteria()), time);

    public static DisplayTarget Target(DisplayGuestOs os = DisplayGuestOs.Linux) => new("agent-1", Guid.NewGuid(), os, "vm-ref");

    public static DisplayBenchRunner Runner(FakeTimeProvider time, DisplayBenchCriteria criteria, params IDisplayBenchScenario[] scenarios) =>
        new(scenarios, Options.Create(criteria), time);
}

internal sealed class FakeTargets : IDisplayBenchTargets
{
    public DisplayTarget? Kiosk { get; set; } = Bench.Target();
    public DisplayTarget? BrowserHost { get; set; } = Bench.Target();
    public Dictionary<DisplaySourceKind, DisplayTarget?> Sources { get; } = new()
    {
        [DisplaySourceKind.Hypervisor] = Bench.Target(),
        [DisplaySourceKind.Guest] = Bench.Target(),
        [DisplaySourceKind.Browser] = Bench.Target(),
    };

    public Task<DisplayTarget?> KioskAsync(CancellationToken ct) => Task.FromResult(Kiosk);

    public Task<DisplayTarget?> BrowserHostAsync(CancellationToken ct) => Task.FromResult(BrowserHost);

    public Task<DisplayTarget?> ForSourceAsync(DisplaySourceKind source, CancellationToken ct) => Task.FromResult(Sources.GetValueOrDefault(source));
}

/// <summary>Returns the queued readings in order, then repeats the last one.</summary>
internal sealed class FakeMemory : IDisplayBenchMemory
{
    private readonly Queue<long> _bytes = new();
    private long _last;

    public FakeMemory(params double[] mib)
    {
        foreach (var m in mib)
        {
            _bytes.Enqueue((long)(m * Bench.Mib));
        }
    }

    public Exception? Throw { get; set; }
    public int Reads { get; private set; }

    public Task<long> ReadGuestMemoryInUseBytesAsync(DisplayTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Reads++;
        if (Throw is not null)
        {
            throw Throw;
        }

        if (_bytes.Count > 0)
        {
            _last = _bytes.Dequeue();
        }

        return Task.FromResult(_last);
    }
}

internal sealed class FakePool : IBrowserPool
{
    public List<PoolSessionRequest> Opened { get; } = [];
    public List<string> Closed { get; } = [];
    public BrowserPoolException? RefuseWith { get; set; }

    public Task<PoolSession> OpenAsync(PoolSessionRequest request, CancellationToken ct)
    {
        if (RefuseWith is not null)
        {
            throw RefuseWith;
        }

        Opened.Add(request);
        var id = "s" + Opened.Count;
        return Task.FromResult(new PoolSession(id, request.AgentId, 0, request.Display, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    public Task CloseAsync(string sessionId, CancellationToken ct)
    {
        Closed.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task<int> CloseAllForAgentAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();

    public IReadOnlyList<PoolSession> Sessions(string agentId) => throw new NotSupportedException();

    public void Touch(string sessionId) => throw new NotSupportedException();

    public PoolSnapshot Snapshot() => throw new NotSupportedException();

    public Task<int> SweepAsync(CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Each capture or input call advances the fake clock by the next latency for its source, so timings are exact.</summary>
internal sealed class FakeDisplay : IGuestDisplay
{
    /// <summary>A recognisable pixel pattern; no report may ever contain it.</summary>
    public static readonly byte[] Pixels = Enumerable.Range(0, 96).Select(i => (byte)(0x80 + (i * 7) % 100)).ToArray();

    private readonly FakeTimeProvider _time;
    private readonly Dictionary<DisplaySourceKind, int> _calls = [];

    public FakeDisplay(FakeTimeProvider time)
    {
        _time = time;
    }

    public HashSet<DisplaySourceKind> Available { get; } = [DisplaySourceKind.Hypervisor, DisplaySourceKind.Guest, DisplaySourceKind.Browser];
    public Dictionary<DisplaySourceKind, double[]> LatencyMs { get; } = new()
    {
        [DisplaySourceKind.Hypervisor] = [100],
        [DisplaySourceKind.Guest] = [100],
        [DisplaySourceKind.Browser] = [100],
    };

    public int FrameBytes { get; set; } = Pixels.Length;
    public Func<DisplaySourceKind, int, Exception?>? FailWhen { get; set; }
    public DisplaySourceKind? WrongSource { get; set; }
    public List<CaptureRequest> Captures { get; } = [];
    public List<InputBatch> Inputs { get; } = [];

    public Task<ScreenFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Captures.Add(request);
        var source = request.Source!.Value;
        Step(source, "capture");
        var png = new byte[FrameBytes];
        Pixels.AsSpan(0, Math.Min(Pixels.Length, FrameBytes)).CopyTo(png);
        return Task.FromResult(new ScreenFrame(png, 1280, 800, WrongSource ?? source, _time.GetUtcNow(), ScreenFrame.ComputeSha256(png)));
    }

    public Task SendInputAsync(InputBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Inputs.Add(batch);
        Step(batch.Source!.Value, "input");
        return Task.CompletedTask;
    }

    public IReadOnlyList<DisplaySourceKind> AvailableSources(DisplayTarget target) => Available.ToArray();

    private void Step(DisplaySourceKind source, string op)
    {
        var n = _calls.GetValueOrDefault(source);
        _calls[source] = n + 1;
        if (FailWhen?.Invoke(source, n) is { } ex)
        {
            throw ex;
        }

        var list = LatencyMs[source];
        _time.Advance(TimeSpan.FromMilliseconds(list[n % list.Length]));
    }
}

internal sealed class FakeTicket : AdmissionTicket
{
    public bool Disposed { get; private set; }

    public override Task ConfirmStartedAsync() => Task.CompletedTask;

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Admits the first <see cref="AdmitCount"/> VMs at once, then leaves the next admission pending and reports it queued.</summary>
internal sealed class FakeGovernor : IDensityGovernor
{
    private readonly FakeTimeProvider _time;
    private bool _queued;

    public FakeGovernor(FakeTimeProvider time, int admitCount)
    {
        _time = time;
        AdmitCount = admitCount;
    }

    public int AdmitCount { get; }
    public long HostFreeMb { get; set; } = 1234;
    public DensityAdmissionException? RefuseAt { get; set; }
    public int RefuseIndex { get; set; } = int.MaxValue;
    public List<DensityVm> Requests { get; } = [];
    public List<FakeTicket> Tickets { get; } = [];

    public Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken)
    {
        var index = Requests.Count;
        Requests.Add(vm);
        if (index == RefuseIndex)
        {
            throw RefuseAt!;
        }

        if (index < AdmitCount)
        {
            var t = new FakeTicket();
            Tickets.Add(t);
            return Task.FromResult<AdmissionTicket>(t);
        }

        _queued = true;
        _time.Advance(TimeSpan.FromHours(1));
        var tcs = new TaskCompletionSource<AdmissionTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public DensitySnapshot Snapshot() => new(HostFreeMb, 0, Tickets.Count, _queued ? 1 : 0, []);

    public void NotifyMemoryChanged()
    {
    }
}
