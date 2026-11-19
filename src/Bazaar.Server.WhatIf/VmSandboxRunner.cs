using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.WhatIf;

/// <summary>
/// Runs one What If branch for real (doc 26 section 3, WP4) through small ports that mirror docs 16 and 17: quota and window, VM host,
/// push gate. Order: request guard, quota, provision, tasks in order, collect, push (sandbox refs only, only on success), teardown in a
/// <c>finally</c>-style step that also runs on cancellation and failure. Failures surface as <see cref="SandboxRunFailedException"/>
/// (stage only) or <see cref="SandboxRequestRejectedException"/>, so no host or guest text reaches the run record.
/// </summary>
public sealed class VmSandboxRunner(ISandboxVmHost host, ISandboxPushGate pushGate, ISandboxQuota quota) : ISandboxRunner
{
    private static readonly TimeSpan _teardownTimeout = TimeSpan.FromSeconds(60);

    public async Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        SandboxRequestGuard.EnsureValid(request);
        ct.ThrowIfCancellationRequested();

        var decision = await quota.TryConsumeAsync(new SandboxQuotaRequest(request.RunId, request.Branch, request.Window), ct).ConfigureAwait(false);
        if (!decision.Granted)
            throw new SandboxRequestRejectedException([$"the sandbox may not start now ({decision.Refusal})"]);

        var spec = new SandboxVmSpec(request.RunId, request.Branch, request.SandboxGcId, SandboxEgress.Denied);
        ISandboxSession session;
        try
        {
            session = await host.ProvisionAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SandboxRunFailedException("provision");
        }

        Exception? failure = null;
        SandboxOutcome? outcome = null;
        try
        {
            outcome = await ExecuteAsync(session, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Teardown ignores the caller's token: a cancelled run must still destroy its VM.
        try
        {
            using var cts = new CancellationTokenSource(_teardownTimeout);
            await session.TeardownAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception) when (failure is not null)
        {
            // The earlier failure is the one to report; teardown errors are host-side and stay out of the run record.
        }
        catch (Exception)
        {
            throw new SandboxRunFailedException("teardown");
        }

        if (failure is not null)
        {
            if (failure is OperationCanceledException or SandboxRunFailedException)
                ExceptionDispatchInfo.Capture(failure).Throw();
            throw new SandboxRunFailedException("run");
        }

        return outcome!;
    }

    private async Task<SandboxOutcome> ExecuteAsync(ISandboxSession session, SandboxRequest request, CancellationToken ct)
    {
        foreach (var task in request.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            SandboxTaskResult result;
            try
            {
                result = await session.RunTaskAsync(task, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SandboxRunFailedException("task");
            }

            if (!result.Completed)
                throw new SandboxRunFailedException("task");
        }

        BranchMeasurement measurement;
        try
        {
            measurement = await session.CollectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SandboxRunFailedException("collect");
        }

        // Only a finished, measured branch is pushed, and only to the sandbox ref the guard already accepted.
        string pushed;
        try
        {
            pushed = await pushGate.PushAsync(new SandboxPushRequest(request.RunId, request.Branch, session.SessionId, request.PushRef), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SandboxRunFailedException("push");
        }

        if (!SandboxRequestGuard.IsSandboxRef(pushed))
            throw new SandboxRunFailedException("push");
        return new SandboxOutcome(request.MetricsSpecHash, measurement, pushed);
    }
}

public static class WhatIfVmRunnerRegistration
{
    /// <summary>
    /// Binds <see cref="VmSandboxRunner"/> as the sandbox runner. Opt in only after a host adapter has bound
    /// <see cref="ISandboxVmHost"/>, <see cref="ISandboxPushGate"/> and <see cref="ISandboxQuota"/>; <c>AddBazaarWhatIf</c> never does this.
    /// </summary>
    public static IServiceCollection AddBazaarWhatIfVmRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISandboxRunner, VmSandboxRunner>();
        return services;
    }
}
