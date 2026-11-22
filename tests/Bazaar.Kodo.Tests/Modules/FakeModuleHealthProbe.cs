using Bazaar.Kodo.Modules;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Modules;

/// <summary>A health contract fake whose check is supplied by the test. The default check reports green.</summary>
internal sealed class FakeModuleHealthProbe : IModuleHealthProbe
{
    private readonly Func<CancellationToken, ValueTask<ProbeResult>> _check;

    public FakeModuleHealthProbe(
        string moduleId,
        Func<CancellationToken, ValueTask<ProbeResult>>? check = null,
        string? probeId = null)
    {
        ModuleId = moduleId;
        ProbeId = probeId ?? ModuleHealthGate.ProbeIdFor(moduleId);
        _check = check ?? (_ => ValueTask.FromResult(ProbeResult.Green()));
    }

    public string ModuleId { get; }

    public string ProbeId { get; }

    public ValueTask<ProbeResult> CheckAsync(CancellationToken ct) => _check(ct);
}

/// <summary>A contract whose <c>ModuleId</c> changes on every read, so it is not stable.</summary>
internal sealed class DriftingIdModuleHealthProbe : IModuleHealthProbe
{
    private int _reads;

    public string ModuleId => $"kodo{++_reads}";

    public string ProbeId => "module.kodo.health";

    public ValueTask<ProbeResult> CheckAsync(CancellationToken ct) => ValueTask.FromResult(ProbeResult.Green());
}
