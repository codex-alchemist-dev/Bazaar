namespace Bazaar.Server.DevSandbox;

/// <summary>
/// Validates, gates, runs and judges one module under test (doc 48 section 2.1). Every gate fails closed: an untested or missing
/// provider, a missing engine, an invalid spec or a production target is a <see cref="SandboxVerdict.Refused"/> run, and there is no
/// unconfined fallback.
/// </summary>
public sealed class SandboxRunner : ISandboxRunner
{
    private const string _zeroHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string _canaryReason = "canary";
    private const string _productionTargetReason = "production-target";
    private const string _dbPrefix = "db:";

    /// <summary>Upper bound on actions audited after the first violation while the killed process drains.</summary>
    private const int _postViolationDrainLimit = 256;

    private readonly ISandboxConfinementProvider[] _providers;
    private readonly ISandboxEngine[] _engines;
    private readonly ISandboxCompanyFactory _companies;
    private readonly Func<ISandboxAuditTrail> _auditFactory;
    private readonly SandboxOs? _hostOs;
    private readonly Action<SandboxRunResult>? _onResult;

    /// <summary>Creates a runner.</summary>
    /// <param name="providers">Registered OS confinement providers.</param>
    /// <param name="engines">Registered execution engines.</param>
    /// <param name="companies">Builds the fake-data company for each run.</param>
    /// <param name="auditFactory">Creates a fresh audit trail per run; defaults to <see cref="HashChainedAuditTrail"/>.</param>
    /// <param name="hostOs">The OS the runner treats as host; defaults to the real OS. On any other OS no provider can match.</param>
    /// <param name="onResult">Called with every run result; its exceptions are swallowed.</param>
    public SandboxRunner(
        IEnumerable<ISandboxConfinementProvider> providers,
        IEnumerable<ISandboxEngine> engines,
        ISandboxCompanyFactory companies,
        Func<ISandboxAuditTrail>? auditFactory = null,
        SandboxOs? hostOs = null,
        Action<SandboxRunResult>? onResult = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(companies);
        _providers = providers.ToArray();
        _engines = engines.ToArray();
        _companies = companies;
        _auditFactory = auditFactory ?? (() => new HashChainedAuditTrail());
        _hostOs = hostOs ?? (OperatingSystem.IsWindows() ? SandboxOs.Windows : OperatingSystem.IsLinux() ? SandboxOs.Linux : null);
        _onResult = onResult;
    }

    /// <inheritdoc />
    public async Task<SandboxRunResult> RunAsync(SandboxRunSpec spec, CancellationToken ct = default)
    {
        var result = await RunCoreAsync(spec, ct).ConfigureAwait(false);
        if (_onResult is not null)
        {
            try
            {
                _onResult(result);
            }
            catch (Exception)
            {
                // A failing sink must never change the verdict.
            }
        }

        return result;
    }

    /// <inheritdoc />
    public SandboxRunResult Replay(SandboxRunSpec spec, IReadOnlyList<SandboxAuditEvent> recorded)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(recorded);
        var runId = spec.RunId ?? string.Empty;
        var refusal = Validate(spec);
        if (refusal is not null)
        {
            return Make(runId, SandboxVerdict.Refused, refusal, [], 0, _zeroHash);
        }

        var head = recorded.Count == 0 ? _zeroHash : recorded[^1].Hash;
        if (!HashChainedAuditTrail.VerifyEvents(recorded))
        {
            return Make(runId, SandboxVerdict.Error, "audit chain broken", [], recorded.Count, head);
        }

        var policy = new SandboxPolicy(spec);
        var violations = new List<SandboxAuditEvent>();
        var canary = false;
        foreach (var e in recorded)
        {
            if (e.Reason == _canaryReason || e.Reason == _productionTargetReason)
            {
                // Canary values and the company schema are not part of the spec; the recorded decision must be a denial.
                if (e.Decision != SandboxDecision.Denied)
                {
                    return Make(runId, SandboxVerdict.ReplayMismatch, $"replay mismatch at sequence {e.Sequence}", violations, recorded.Count, head);
                }

                canary |= e.Reason == _canaryReason;
                violations.Add(e);
                continue;
            }

            var expected = IsForeignDbTarget(e.Action) ? SandboxDecision.Denied : policy.Decide(e.Action, out _);
            if (expected != e.Decision)
            {
                return Make(runId, SandboxVerdict.ReplayMismatch, $"replay mismatch at sequence {e.Sequence}", violations, recorded.Count, head);
            }

            if (e.Decision == SandboxDecision.Denied)
            {
                violations.Add(e);
            }
        }

        if (canary)
        {
            return Make(runId, SandboxVerdict.CanaryTriggered, "a canary value left the sandbox", violations, recorded.Count, head);
        }

        return violations.Count > 0
            ? Make(runId, SandboxVerdict.UndeclaredAccess, "an action had no approved grant", violations, recorded.Count, head)
            : Make(runId, SandboxVerdict.Clean, string.Empty, violations, recorded.Count, head);
    }

    private static SandboxRunResult Make(
        string runId, SandboxVerdict verdict, string reason, IReadOnlyList<SandboxAuditEvent> violations, long count, string head) =>
        new(runId, verdict, reason, violations, count, head);

    private static string? Validate(SandboxRunSpec spec)
    {
        try
        {
            return ProductionIsolationGuard.ValidateSpec(spec);
        }
        catch (Exception)
        {
            return "spec validation failed";
        }
    }

    private static bool IsDbAction(SandboxAction a) =>
        (a.Kind == SandboxActionKind.FileAccess || a.Kind == SandboxActionKind.HostCall)
        && (a.Target ?? string.Empty).StartsWith(_dbPrefix, StringComparison.Ordinal);

    /// <summary>Replay has no company: a db target is acceptable only when it names some sandbox schema.</summary>
    private static bool IsForeignDbTarget(SandboxAction a) =>
        IsDbAction(a) && !ProductionIsolationGuard.IsSandboxSchema(SchemaOf(a.Target));

    private static string SchemaOf(string target)
    {
        var rest = target[_dbPrefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? rest : rest[..dot];
    }

    private static bool TargetsOtherSchema(SandboxAction a, string schema)
    {
        if (!IsDbAction(a))
        {
            return false;
        }

        var s = SchemaOf(a.Target);
        return !ProductionIsolationGuard.IsSandboxSchema(s) || !string.Equals(s, schema, StringComparison.Ordinal);
    }

    private async Task<SandboxRunResult> RunCoreAsync(SandboxRunSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var runId = spec.RunId ?? string.Empty;

        var refusal = Validate(spec);
        if (refusal is not null)
        {
            return Make(runId, SandboxVerdict.Refused, refusal, [], 0, _zeroHash);
        }

        if (_hostOs is not { } os)
        {
            return Make(runId, SandboxVerdict.Refused, "no tested confinement provider for this OS", [], 0, _zeroHash);
        }

        var provider = _providers.FirstOrDefault(p => p.Os == os && p.Tested);
        if (provider is null)
        {
            return Make(runId, SandboxVerdict.Refused, $"no tested confinement provider for {os}", [], 0, _zeroHash);
        }

        var engine = _engines.FirstOrDefault(e => e.Kind == spec.Engine);
        if (engine is null)
        {
            return Make(runId, SandboxVerdict.Refused, $"no engine registered for {spec.Engine}", [], 0, _zeroHash);
        }

        SandboxTestCompany company;
        try
        {
            company = _companies.Create(spec);
        }
        catch (Exception ex)
        {
            return Make(runId, SandboxVerdict.Error, $"company generation failed: {ex.GetType().Name}", [], 0, _zeroHash);
        }

        if (!ProductionIsolationGuard.IsSandboxSchema(company.Schema))
        {
            return Make(runId, SandboxVerdict.Refused, "test company schema is not a sandbox schema", [], 0, _zeroHash);
        }

        var trail = _auditFactory();
        var policy = new SandboxPolicy(spec);
        var violations = new List<SandboxAuditEvent>();
        ISandboxProcess? process = null;
        var verdict = SandboxVerdict.Clean;
        var reason = string.Empty;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)));
            try
            {
                process = await provider.StartAsync(spec, cts.Token).ConfigureAwait(false);
                var afterViolation = 0;
                await foreach (var action in engine.RunAsync(spec, process, company, cts.Token).ConfigureAwait(false))
                {
                    var canary = CanaryDetector.Find(action.Payload ?? string.Empty, company.Canaries)
                        ?? CanaryDetector.Find(action.Target ?? string.Empty, company.Canaries);
                    SandboxDecision decision;
                    string why;
                    if (canary is not null)
                    {
                        decision = SandboxDecision.Denied;
                        why = _canaryReason;
                    }
                    else if (TargetsOtherSchema(action, company.Schema))
                    {
                        decision = SandboxDecision.Denied;
                        why = _productionTargetReason;
                    }
                    else
                    {
                        decision = policy.Decide(action, out why);
                    }

                    var ev = trail.Append(action, decision, why);
                    if (decision == SandboxDecision.Allowed)
                    {
                        // After a violation the process is dead; keep auditing what was already in flight, within a bound.
                        if (verdict != SandboxVerdict.Clean && ++afterViolation > _postViolationDrainLimit)
                        {
                            break;
                        }

                        continue;
                    }

                    violations.Add(ev);
                    if (canary is not null)
                    {
                        verdict = SandboxVerdict.CanaryTriggered;
                        reason = "a canary value left the sandbox";
                        await KillQuietlyAsync(process).ConfigureAwait(false);
                        break;
                    }

                    if (verdict == SandboxVerdict.Clean)
                    {
                        verdict = SandboxVerdict.UndeclaredAccess;
                        reason = "an action had no approved grant";
                        await KillQuietlyAsync(process).ConfigureAwait(false);
                    }
                    else if (++afterViolation > _postViolationDrainLimit)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (verdict == SandboxVerdict.Clean)
                {
                    verdict = SandboxVerdict.TimedOut;
                    reason = "run exceeded its wall-clock limit";
                }
            }
            catch (Exception ex)
            {
                // Only the type name: the message could hold canaries.
                if (verdict == SandboxVerdict.Clean)
                {
                    verdict = SandboxVerdict.Error;
                    reason = $"run failed: {ex.GetType().Name}";
                }
            }
        }
        finally
        {
            if (process is not null)
            {
                await KillQuietlyAsync(process).ConfigureAwait(false);
                try
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Disposal errors never change the verdict.
                }
            }
        }

        if (!trail.Verify())
        {
            return Make(runId, SandboxVerdict.Error, "audit chain broken", violations, trail.Events.Count, trail.HeadHash);
        }

        return Make(runId, verdict, reason, violations, trail.Events.Count, trail.HeadHash);
    }

    private static async Task KillQuietlyAsync(ISandboxProcess process)
    {
        try
        {
            await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Kill is best effort here; the finally block retries once more.
        }
    }
}
