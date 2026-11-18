using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Bazaar.Server.DevSandbox.Wasm;

/// <summary>Tunable limits and names for <see cref="WasmSandboxEngine"/>.</summary>
public sealed record WasmEngineOptions
{
    /// <summary>The exported function to run.</summary>
    public string EntryPoint { get; init; } = "run";

    /// <summary>Instruction (fuel) budget per run.</summary>
    public long Fuel { get; init; } = 100_000_000;

    /// <summary>Most host import calls one run may make.</summary>
    public int MaxHostCalls { get; init; } = 10_000;
}

/// <summary>
/// Runs a WebAssembly module with no host imports except the granted permissions (doc 48 section 2). It never decides policy: it
/// reports each attempted action to the runner and services a host call only after the runner has asked for the next action.
/// </summary>
/// <remarks>
/// Flow: (1) fetch module bytes; (2) read declared imports; (3) an import not in <see cref="WasmImportCatalogue"/> or whose
/// permission class the spec does not grant is not linked, is reported as a <see cref="SandboxActionKind.PermissionCheck"/>
/// action, and the module is then never instantiated; (4) otherwise the module runs, and each import call is yielded as an action
/// and parked until the consumer moves on. If the consumer stops instead (it denied the action and killed the run), the call is
/// abandoned and never serviced. Because the runner keeps enumerating after a denial, the engine also refuses to service a call its
/// own conservative check of the spec's grants does not cover; that check can only withhold, never allow.
/// Host import results come only from <see cref="SandboxTestCompany.Records"/>.
/// </remarks>
public sealed class WasmSandboxEngine : ISandboxEngine
{
    private readonly IWasmRuntime _runtime;
    private readonly IWasmModuleSource _modules;
    private readonly WasmEngineOptions _options;

    /// <summary>Creates the engine.</summary>
    /// <param name="runtime">The WebAssembly runtime.</param>
    /// <param name="modules">Where module bytes come from.</param>
    /// <param name="options">Limits and entry point; defaults when null.</param>
    public WasmSandboxEngine(IWasmRuntime runtime, IWasmModuleSource modules, WasmEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(modules);
        _runtime = runtime;
        _modules = modules;
        _options = options ?? new WasmEngineOptions();
    }

    /// <inheritdoc />
    public SandboxEngineKind Kind => SandboxEngineKind.Wasm;

    /// <inheritdoc />
    public async IAsyncEnumerable<SandboxAction> RunAsync(
        SandboxRunSpec spec, ISandboxProcess process, SandboxTestCompany company, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(company);
        if (spec.MemoryLimitMb <= 0 || _options.Fuel <= 0 || _options.MaxHostCalls <= 0)
        {
            throw new InvalidOperationException("wasm limits must be positive");
        }

        var bytes = await _modules.GetModuleAsync(spec.ModuleId, spec.ModuleVersion, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("module not found");
        var declared = _runtime.GetDeclaredImports(bytes);

        var linked = new Dictionary<string, WasmImportInfo>(StringComparer.Ordinal);
        var refused = new List<SandboxAction>();
        foreach (var import in declared)
        {
            var key = import.Key;
            if (!WasmImportCatalogue.TryGet(key, out var info))
            {
                refused.Add(new SandboxAction(SandboxActionKind.PermissionCheck, WasmImportCatalogue.UnknownImportClass, key));
            }
            else if (!spec.Grants.Any(g => string.Equals(g.Class, info.Class, StringComparison.Ordinal)))
            {
                refused.Add(new SandboxAction(SandboxActionKind.PermissionCheck, info.Class, key));
            }
            else
            {
                linked[key] = info;
            }
        }

        if (refused.Count > 0)
        {
            // Fail closed: report every refused import and instantiate nothing.
            foreach (var action in refused)
            {
                yield return action;
            }

            yield break;
        }

        var calls = Channel.CreateUnbounded<PendingCall>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var host = new Bridge(linked, calls.Writer, _options.MaxHostCalls);
        var limits = new WasmLimits(checked(spec.MemoryLimitMb * 1024L * 1024L), _options.Fuel);
        var linkedSet = declared.ToArray();
        var run = Task.Run(
            async () =>
            {
                try
                {
                    return await _runtime.RunAsync(bytes, linkedSet, host, limits, _options.EntryPoint, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    calls.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var call in calls.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return call.Action;

                // Reaching here means the consumer asked for the next action instead of stopping. Withhold anything the grants do not cover.
                if (!Covered(spec, call.Action))
                {
                    call.Result.TrySetException(new InvalidOperationException("host call not covered by a grant"));
                    throw new InvalidOperationException("host call not covered by a grant");
                }

                call.Result.TrySetResult(Service(call, company));
            }

            var outcome = await run.ConfigureAwait(false);
            if (outcome.Status != WasmRunStatus.Completed)
            {
                throw new InvalidOperationException($"wasm run ended: {outcome.Status}");
            }
        }
        finally
        {
            // Abandon the run: parked calls are cancelled, never serviced.
            await cts.CancelAsync().ConfigureAwait(false);
            host.AbandonAll();
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The outcome of an abandoned run is irrelevant; the verdict comes from the audit stream.
            }
        }
    }

    private static string Service(PendingCall call, SandboxTestCompany company) => call.Info.Kind switch
    {
        SandboxActionKind.NetworkAttempt => "stub-response",
        _ => company.Records.TryGetValue(call.Action.Target, out var value) ? value : string.Empty,
    };

    private static bool Covered(SandboxRunSpec spec, SandboxAction action)
    {
        if (action.Kind == SandboxActionKind.PermissionCheck)
        {
            return false;
        }

        if (action.Kind == SandboxActionKind.NetworkAttempt
            && !spec.EgressAllowlist.Any(h => string.Equals(h, action.Target, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return spec.Grants.Any(g => string.Equals(g.Class, action.Class, StringComparison.Ordinal) && TargetMatches(g.Target, action.Target));
    }

    private static bool TargetMatches(string grant, string target)
    {
        if (grant == "*")
        {
            return true;
        }

        return grant.EndsWith("/*", StringComparison.Ordinal)
            ? target.StartsWith(grant[..^1], StringComparison.Ordinal)
            : string.Equals(grant, target, StringComparison.Ordinal);
    }

    private sealed class PendingCall(SandboxAction action, WasmImportInfo info)
    {
        public SandboxAction Action { get; } = action;

        public WasmImportInfo Info { get; } = info;

        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Bridge(IReadOnlyDictionary<string, WasmImportInfo> linked, ChannelWriter<PendingCall> writer, int maxCalls) : IWasmHostImports
    {
        private readonly List<PendingCall> _parked = [];
        private int _count;
        private bool _closed;

        public async ValueTask<string> CallAsync(string importName, string argument, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(importName);
            argument ??= string.Empty;
            if (!linked.TryGetValue(importName, out var info))
            {
                throw new InvalidOperationException("import is not linked");
            }

            if (Interlocked.Increment(ref _count) > maxCalls)
            {
                throw new InvalidOperationException("host call limit exceeded");
            }

            // For network calls the first line is the host and the rest is the body; other imports pass the target whole.
            var target = argument;
            var payload = string.Empty;
            if (info.Kind == SandboxActionKind.NetworkAttempt)
            {
                var nl = argument.IndexOf('\n', StringComparison.Ordinal);
                if (nl >= 0)
                {
                    target = argument[..nl];
                    payload = argument[(nl + 1)..];
                }
            }

            var call = new PendingCall(new SandboxAction(info.Kind, info.Class, target, payload), info);
            lock (_parked)
            {
                if (_closed)
                {
                    throw new OperationCanceledException();
                }

                _parked.Add(call);
            }

            await writer.WriteAsync(call, ct).ConfigureAwait(false);
            return await call.Result.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void AbandonAll()
        {
            lock (_parked)
            {
                _closed = true;
                foreach (var c in _parked)
                {
                    c.Result.TrySetCanceled();
                }
            }
        }
    }
}
