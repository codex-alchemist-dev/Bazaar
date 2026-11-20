using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Souls;

/// <summary>
/// The Souls binding module's health contract (doc 46 section 10, D10). The self-test is the reconciliation: any Soul stamp in
/// the GC log that names a missing version fails it, and Kodo reports that.
/// </summary>
public sealed class SoulBindingsHealth(SoulStampReconciler reconciler, TimeProvider? time = null) : IModuleHealthContract
{
    /// <summary>The module id Kodo reports under.</summary>
    public const string Id = "souls.bindings";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public ValueTask<ModuleHealthCheck> LivenessAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Check(ModuleHealthStatus.Healthy, "alive"));

    /// <inheritdoc />
    public ValueTask<ModuleHealthCheck> ReadinessAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Check(ModuleHealthStatus.Healthy, "ready"));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModuleDependencyHealth>> DependenciesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ModuleDependencyHealth>>([]);

    /// <inheritdoc />
    public async ValueTask<ModuleHealthCheck> SelfTestAsync(ModuleSelfTestScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var dangling = await reconciler.FindDanglingAsync(SoulStampReconciler.DefaultLimit, cancellationToken).ConfigureAwait(false);
            return dangling.Count == 0
                ? Check(ModuleHealthStatus.Healthy, "every Soul stamp in the GC log names a real version")
                : Check(ModuleHealthStatus.Failing, $"{dangling.Count} Soul stamp(s) in the GC log name a missing version");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Check(ModuleHealthStatus.Unknown, "the reconciliation could not read the GC log");
        }
    }

    private ModuleHealthCheck Check(ModuleHealthStatus status, string reason) => new(status, reason, _time.GetUtcNow());
}
