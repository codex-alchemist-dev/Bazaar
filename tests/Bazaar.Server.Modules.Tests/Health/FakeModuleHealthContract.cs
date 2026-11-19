using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Modules.Tests.Health;

/// <summary>A test module whose four probes can be swapped per test. Each default answers Healthy.</summary>
internal sealed class FakeModuleHealthContract(string moduleId) : IModuleHealthContract
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public string ModuleId { get; } = moduleId;

    public Func<CancellationToken, ValueTask<ModuleHealthCheck>> LivenessProbe { get; set; } = Ok;

    public Func<CancellationToken, ValueTask<ModuleHealthCheck>> ReadinessProbe { get; set; } = Ok;

    public Func<CancellationToken, ValueTask<IReadOnlyList<ModuleDependencyHealth>>> DependenciesProbe { get; set; } =
        _ => ValueTask.FromResult<IReadOnlyList<ModuleDependencyHealth>>([]);

    public Func<ModuleSelfTestScope, CancellationToken, ValueTask<ModuleHealthCheck>> SelfTestProbe { get; set; } =
        (_, _) => Ok(default);

    public ValueTask<ModuleHealthCheck> LivenessAsync(CancellationToken cancellationToken) => LivenessProbe(cancellationToken);

    public ValueTask<ModuleHealthCheck> ReadinessAsync(CancellationToken cancellationToken) => ReadinessProbe(cancellationToken);

    public ValueTask<IReadOnlyList<ModuleDependencyHealth>> DependenciesAsync(CancellationToken cancellationToken) =>
        DependenciesProbe(cancellationToken);

    public ValueTask<ModuleHealthCheck> SelfTestAsync(ModuleSelfTestScope scope, CancellationToken cancellationToken) =>
        SelfTestProbe(scope, cancellationToken);

    private static ValueTask<ModuleHealthCheck> Ok(CancellationToken _) =>
        ValueTask.FromResult(new ModuleHealthCheck(ModuleHealthStatus.Healthy, "ok", _at));
}
