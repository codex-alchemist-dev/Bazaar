using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Portability;

/// <summary>
/// What the host reads about import jobs for health (doc 44 section 11). The host implements it over the job store, because
/// <see cref="IImportJobStore"/> has no listing. Counts only: no job ids, actors or reasons leave through health.
/// </summary>
/// <param name="FailedVerifyJobs">Jobs in <see cref="ImportJobState.FailedVerify"/>.</param>
/// <param name="StalledJobs">Jobs in <see cref="ImportJobState.Stalled"/>.</param>
/// <param name="CrashLoopingImporters">Ids of importers whose parser is in a crash loop.</param>
public sealed record ImportJobHealthSnapshot(int FailedVerifyJobs, int StalledJobs, IReadOnlyList<string> CrashLoopingImporters);

/// <summary>The host's read of job status for <see cref="PortabilityHealth"/>.</summary>
public interface IImportJobHealthSource
{
    /// <summary>The current counts. May use cached state; the readiness budget is 500 ms (doc 46 section 10).</summary>
    ValueTask<ImportJobHealthSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>One declared dependency of the portability module (for example the parser sandbox runner), probed by its owner.</summary>
public interface IPortabilityDependencyProbe
{
    /// <summary>The dependency id, as declared in the module manifest.</summary>
    string DependencyId { get; }

    /// <summary>The dependency's current status. Should not throw; <see cref="PortabilityHealth"/> converts an escape to Unknown.</summary>
    ValueTask<ModuleHealthCheck> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>An alert Kodo raises from import job status (doc 44 section 11): the kind names the condition.</summary>
public sealed record PortabilityAlert(string Kind, string Reason);

/// <summary>Pure mapping from a job status snapshot to the alerts of doc 44 section 11.</summary>
public static class PortabilityJobAlerts
{
    /// <summary>Alert kind for a job that failed verification.</summary>
    public const string FailedVerify = "failed_verify";

    /// <summary>Alert kind for a job with no progress past the stall threshold.</summary>
    public const string Stalled = "stalled";

    /// <summary>Alert kind for an importer whose parser keeps crashing.</summary>
    public const string ParserCrashLoop = "parser_crash_loop";

    /// <summary>The alerts the snapshot calls for, in a fixed order. Empty when nothing is wrong.</summary>
    public static IReadOnlyList<PortabilityAlert> Evaluate(ImportJobHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var alerts = new List<PortabilityAlert>();
        if (snapshot.FailedVerifyJobs > 0) alerts.Add(new(FailedVerify, $"{snapshot.FailedVerifyJobs} import job(s) failed verification"));
        if (snapshot.StalledJobs > 0) alerts.Add(new(Stalled, $"{snapshot.StalledJobs} import job(s) stalled"));
        if (snapshot.CrashLoopingImporters.Count > 0)
        {
            alerts.Add(new(ParserCrashLoop, $"{snapshot.CrashLoopingImporters.Count} importer parser(s) in a crash loop"));
        }
        return alerts;
    }
}

/// <summary>
/// The portability module's health contract (doc 44 sections 9 and 11, doc 46 section 10). Liveness does no I/O. Readiness
/// reports job alerts as Degraded: import trouble does not stop the module taking work. Dependencies are the probes the host
/// registers (the parser sandbox runner, for example). The self-test runs every importer's built-in fixture self-test; it
/// reads nothing outside the process, so Quick and Full are the same and leave no residue.
/// </summary>
public sealed class PortabilityHealth(
    IEnumerable<IMappingImporter> importers,
    IImportJobHealthSource? jobs = null,
    IEnumerable<IPortabilityDependencyProbe>? dependencies = null,
    TimeProvider? time = null) : IModuleHealthContract
{
    /// <summary>The module id Kodo reports under.</summary>
    public const string Id = "portability";

    private readonly IReadOnlyList<IMappingImporter> _importers = [.. importers];
    private readonly IReadOnlyList<IPortabilityDependencyProbe> _dependencies = [.. dependencies ?? []];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public ValueTask<ModuleHealthCheck> LivenessAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Check(ModuleHealthStatus.Healthy, "alive"));

    /// <inheritdoc />
    public async ValueTask<ModuleHealthCheck> ReadinessAsync(CancellationToken cancellationToken)
    {
        if (jobs is null) return Check(ModuleHealthStatus.Healthy, "ready");
        try
        {
            var alerts = PortabilityJobAlerts.Evaluate(await jobs.ReadAsync(cancellationToken).ConfigureAwait(false));
            return alerts.Count == 0
                ? Check(ModuleHealthStatus.Healthy, "ready")
                : Check(ModuleHealthStatus.Degraded, string.Join("; ", alerts.Select(a => a.Reason)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Check(ModuleHealthStatus.Unknown, "import job status could not be read");
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ModuleDependencyHealth>> DependenciesAsync(CancellationToken cancellationToken)
    {
        var result = new List<ModuleDependencyHealth>(_dependencies.Count);
        foreach (var dependency in _dependencies)
        {
            try
            {
                result.Add(new(dependency.DependencyId, await dependency.ProbeAsync(cancellationToken).ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                result.Add(new(dependency.DependencyId, Check(ModuleHealthStatus.Unknown, "the dependency probe failed")));
            }
        }
        return result;
    }

    /// <inheritdoc />
    public ValueTask<ModuleHealthCheck> SelfTestAsync(ModuleSelfTestScope scope, CancellationToken cancellationToken)
    {
        // A self-test that runs nothing must not pass (doc 46 section 10.3 item 7).
        if (_importers.Count == 0) return ValueTask.FromResult(Check(ModuleHealthStatus.Unknown, "no importer is registered to test"));

        var failed = new List<string>();
        foreach (var importer in _importers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool ok;
            try
            {
                ok = importer.SelfTest().Ok;
            }
            catch (Exception)
            {
                ok = false;
            }
            if (!ok) failed.Add(importer.Id);
        }

        return ValueTask.FromResult(failed.Count == 0
            ? Check(ModuleHealthStatus.Healthy, $"{_importers.Count} importer fixture self-test(s) passed")
            : Check(ModuleHealthStatus.Failing, "importer self-test failed for: " + string.Join(", ", failed)));
    }

    private ModuleHealthCheck Check(ModuleHealthStatus status, string reason) => new(status, reason, _time.GetUtcNow());
}
