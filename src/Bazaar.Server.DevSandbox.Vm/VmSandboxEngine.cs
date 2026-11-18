using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Bazaar.Server.DevSandbox.Vm;

/// <summary>Options for <see cref="VmSandboxEngine"/>.</summary>
public sealed record VmEngineOptions
{
    /// <summary>The only suffix an egress host may have (doc 48 section 2.1 rule 4: named test endpoints).</summary>
    public string TestHostSuffix { get; init; } = ".test";

    /// <summary>Called with (VM id, step name, exception) when a teardown step fails, so the VM may still exist. Its exceptions are swallowed.</summary>
    public Action<string, string, Exception>? OnCleanupFailure { get; init; }
}

/// <summary>
/// Runs a module that needs native code or a runtime in a disposable sandbox-profile VM (doc 48 section 2.1, VM engine). It never
/// decides policy: it yields what the guest reports and the runner decides. Order, all fail closed: create, attach the two mounts,
/// apply egress (or confirm block-all), start, run. Any failure before the run means nothing starts. Teardown always runs, even on
/// cancel or a violation: block all egress, stop, detach all, retire. A VM is never reused.
/// </summary>
public sealed class VmSandboxEngine : ISandboxEngine
{
    private readonly ISandboxVmModuleSource _modules;
    private readonly ISandboxVmHost _host;
    private readonly ISandboxVmMounts _mounts;
    private readonly ISandboxVmEgress _egress;
    private readonly ISandboxVmGuestChannel _guest;
    private readonly VmEngineOptions _options;
    private readonly ConcurrentDictionary<string, byte> _usedVmIds = new(StringComparer.Ordinal);

    /// <summary>Creates the engine.</summary>
    /// <param name="modules">Image, bundle and entry command by module.</param>
    /// <param name="host">VM lifecycle.</param>
    /// <param name="mounts">The two allowed mounts.</param>
    /// <param name="egress">Egress control.</param>
    /// <param name="guest">The guest channel.</param>
    /// <param name="options">Options; defaults when null.</param>
    public VmSandboxEngine(
        ISandboxVmModuleSource modules,
        ISandboxVmHost host,
        ISandboxVmMounts mounts,
        ISandboxVmEgress egress,
        ISandboxVmGuestChannel guest,
        VmEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(egress);
        ArgumentNullException.ThrowIfNull(guest);
        _modules = modules;
        _host = host;
        _mounts = mounts;
        _egress = egress;
        _guest = guest;
        _options = options ?? new VmEngineOptions();
    }

    /// <inheritdoc />
    public SandboxEngineKind Kind => SandboxEngineKind.Vm;

    /// <inheritdoc />
    public async IAsyncEnumerable<SandboxAction> RunAsync(
        SandboxRunSpec spec, ISandboxProcess process, SandboxTestCompany company, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(company);
        if (spec.MemoryLimitMb <= 0)
        {
            throw new InvalidOperationException("vm limits must be positive");
        }

        var hosts = ValidateEgress(spec.EgressAllowlist);
        var plan = await _modules.GetPlanAsync(spec.ModuleId, spec.ModuleVersion, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("module not found");
        if (string.IsNullOrWhiteSpace(plan.Image) || string.IsNullOrWhiteSpace(plan.BundleRef) || string.IsNullOrWhiteSpace(plan.EntryCommand))
        {
            throw new InvalidOperationException("module plan is incomplete");
        }

        var vm = await _host.CreateAsync(new SandboxVmRequest(spec.RunId, plan.Os, plan.Image, spec.MemoryLimitMb), ct).ConfigureAwait(false);
        try
        {
            if (vm is null || string.IsNullOrEmpty(vm.VmId) || !_usedVmIds.TryAdd(vm.VmId, 0))
            {
                throw new InvalidOperationException("sandbox VM was reused or has no id");
            }

            await _mounts.AttachModuleBundleAsync(vm, plan.BundleRef, ct).ConfigureAwait(false);
            await _mounts.AttachScratchAsync(vm, company.Records, ct).ConfigureAwait(false);

            // Egress must be in place before the module can run: an allowlist, or a confirmed block-all.
            if (hosts.Count == 0)
            {
                await _egress.BlockAllAsync(vm, ct).ConfigureAwait(false);
            }
            else
            {
                await _egress.ApplyAsync(vm, hosts, ct).ConfigureAwait(false);
            }

            await _host.StartAsync(vm, ct).ConfigureAwait(false);

            await foreach (var action in _guest.RunAsync(vm, plan, ct).ConfigureAwait(false))
            {
                yield return action;
            }
        }
        finally
        {
            if (vm is not null && !string.IsNullOrEmpty(vm.VmId))
            {
                await TearDownAsync(vm).ConfigureAwait(false);
            }
        }
    }

    private List<string> ValidateEgress(IReadOnlyList<string> allowlist)
    {
        var hosts = new List<string>(allowlist.Count);
        foreach (var raw in allowlist)
        {
            var host = raw?.Trim() ?? string.Empty;
            if (host.Length <= _options.TestHostSuffix.Length
                || !host.EndsWith(_options.TestHostSuffix, StringComparison.OrdinalIgnoreCase)
                || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            {
                throw new InvalidOperationException("egress host is not a plain test host");
            }

            hosts.Add(host);
        }

        return hosts;
    }

    private async Task TearDownAsync(SandboxVmHandle vm)
    {
        // Independent best-effort steps with no cancellation: one failing step never skips the others, and retire always runs.
        await StepAsync("block-all", vm, _egress.BlockAllAsync).ConfigureAwait(false);
        await StepAsync("stop", vm, _host.StopAsync).ConfigureAwait(false);
        await StepAsync("detach", vm, _mounts.DetachAllAsync).ConfigureAwait(false);
        if (!await StepAsync("retire", vm, _host.RetireAsync).ConfigureAwait(false))
        {
            await StepAsync("retire", vm, _host.RetireAsync).ConfigureAwait(false);
        }
    }

    private async Task<bool> StepAsync(string name, SandboxVmHandle vm, Func<SandboxVmHandle, CancellationToken, Task> step)
    {
        try
        {
            await step(vm, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                _options.OnCleanupFailure?.Invoke(vm.VmId, name, ex);
            }
            catch (Exception)
            {
                // The failure callback must never mask the run.
            }

            return false;
        }
    }
}
