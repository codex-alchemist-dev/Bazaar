using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// Runs the kill switch drill against one synthetic drill agent (doc 15, doc 48 section 11): a baseline, an engaged agent stop, a
/// check that the stop refuses every effect class, a check of the containment effects, a signed release, and a check that the
/// agent runs again. The steps go through the public <see cref="KillSwitchEngine"/> API and the <see cref="IAgentContainment"/>
/// challenge, so the drill exercises the production path.
/// </summary>
/// <remarks>
/// The runner stops only agents that <see cref="IDrillTarget.IsDrillAgent"/> accepts, and it does not run while a global stop is
/// engaged, since a drill then proves nothing and could hide a real incident. The cleanup runs in a finally block: if the drill
/// agent is still stopped when the run ends, for any reason, the cleanup releases it with a fresh challenge. A failing step ends
/// the sequence and is reported; a cancellation is rethrown after the cleanup and produces no result.
/// </remarks>
public sealed class DrillRunner(
    KillSwitchEngine engine,
    IAgentContainment containment,
    IDrillProbe probe,
    IDrillTarget target,
    TimeProvider? timeProvider = null)
{
    /// <summary>Actor recorded on the drill's engage record. It is not an owner.</summary>
    public const string DrillActorId = "system:drill";

    private const string _reason = "Weekly kill switch drill (synthetic agent).";
    private const string _cleanupStep = "cleanup";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Runs the drill against <paramref name="agentId"/> and returns its result.</summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is not a drill agent. Nothing is stopped.</exception>
    /// <exception cref="InvalidOperationException">A global stop is engaged. Nothing is stopped.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled. The drill agent is released first.</exception>
    public async Task<DrillResult> RunAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!target.IsDrillAgent(agentId))
        {
            throw new ArgumentException("Only a drill agent can be stopped by the drill.", nameof(agentId));
        }

        if (engine.GetStatus().Global is not null)
        {
            throw new InvalidOperationException("A global stop is engaged; the drill does not run while it is.");
        }

        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        var steps = new List<DrillStepResult>();
        try
        {
            await RunStepsAsync(agentId, steps, ct).ConfigureAwait(false);
        }
        finally
        {
            // Runs on success, failure and cancellation. CancellationToken.None: the release must not be skipped by the caller's token.
            var cleanup = await CleanupAsync(agentId).ConfigureAwait(false);
            if (cleanup is not null)
            {
                steps.Add(cleanup);
            }
        }

        return new DrillResult(Guid.CreateVersion7(startedAt), startedAt, _time.GetElapsedTime(started), steps);
    }

    /// <summary>Runs the steps in order and stops at the first failure.</summary>
    private async Task RunStepsAsync(string agentId, List<DrillStepResult> steps, CancellationToken ct)
    {
        if (!await StepAsync(steps, "baseline", async () =>
        {
            if (IsStopped(agentId))
            {
                return "The drill agent was already stopped before the drill.";
            }

            var observed = await probe.ObserveAsync(agentId, ct).ConfigureAwait(false);
            return observed.IsOpen ? null : "Containment effects were present before the drill.";
        }, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await StepAsync(steps, "engage", async () =>
        {
            await engine.EngageAgentAsync(DrillActorId, agentId, _reason, ct).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await StepAsync(steps, "stop-visible", () => Task.FromResult(CheckStopVisible(agentId)), ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await StepAsync(steps, "containment", async () =>
        {
            var observed = await probe.ObserveAsync(agentId, ct).ConfigureAwait(false);
            if (!observed.TokensRevoked)
            {
                return "Tokens were not revoked.";
            }

            if (!observed.VmsPaused)
            {
                return "The VMs were not paused.";
            }

            return observed.EgressBlocked ? null : "Egress was not blocked.";
        }, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await StepAsync(steps, "release", () => ReleaseAsync(agentId, ct), ct).ConfigureAwait(false))
        {
            return;
        }

        await StepAsync(steps, "released", async () =>
        {
            if (IsStopped(agentId))
            {
                return "The drill agent is still stopped after the release.";
            }

            if (!engine.Evaluate(agentId, ToolEffect.Write).Allowed)
            {
                return "Write calls are still refused after the release.";
            }

            var observed = await probe.ObserveAsync(agentId, ct).ConfigureAwait(false);
            return observed.IsOpen ? null : "Containment effects remain after the release.";
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Records one step. A thrown exception fails the step with its type name only, since its message can carry details.</summary>
    /// <returns>True when the step passed.</returns>
    private static async Task<bool> StepAsync(List<DrillStepResult> steps, string name, Func<Task<string?>> check, CancellationToken ct)
    {
        string? failure;
        try
        {
            failure = await check().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failure = $"The step threw {ex.GetType().Name}.";
        }

        steps.Add(failure is null ? new DrillStepResult(name, true, "Passed.") : new DrillStepResult(name, false, failure));
        return failure is null;
    }

    private string? CheckStopVisible(string agentId)
    {
        if (!IsStopped(agentId))
        {
            return "The engine does not report the drill agent as stopped.";
        }

        foreach (var effect in Enum.GetValues<ToolEffect>())
        {
            var decision = engine.Evaluate(agentId, effect);
            if (decision.Allowed || decision.Scope != KillSwitchScope.Agent)
            {
                return $"A {effect} call was not refused by the agent stop.";
            }
        }

        return null;
    }

    /// <summary>Releases the agent with a fresh challenge and a signature from the drill device. Throws when the containment denies it.</summary>
    /// <returns>Null on success, or the failure reason.</returns>
    private async Task<string?> ReleaseAsync(string agentId, CancellationToken ct)
    {
        var challenge = await containment.IssueReleaseChallengeAsync(agentId, target.DrillDeviceId, ct).ConfigureAwait(false);
        if (challenge is null)
        {
            return "No release challenge was issued to the drill device.";
        }

        var request = await target.SignReleaseAsync(agentId, target.DrillDeviceId, challenge, ct).ConfigureAwait(false);
        if (request is null)
        {
            return "The drill device could not sign the release.";
        }

        if (request.AgentId != agentId || request.DeviceId != target.DrillDeviceId)
        {
            return "The signed release does not match the drill agent and device.";
        }

        await engine.ResumeAgentAsync(agentId, _reason, request, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Releases the drill agent if it is still stopped at the end of the run. Failures are reported as a step, never thrown, so the
    /// finally block cannot hide the drill's own result.
    /// </summary>
    private async Task<DrillStepResult?> CleanupAsync(string agentId)
    {
        if (!IsStopped(agentId))
        {
            return null;
        }

        try
        {
            var failure = await ReleaseAsync(agentId, CancellationToken.None).ConfigureAwait(false);
            if (failure is null && IsStopped(agentId))
            {
                failure = "The drill agent is still stopped after the cleanup.";
            }

            return failure is null
                ? new DrillStepResult(_cleanupStep, true, "Released the drill agent after the run.")
                : new DrillStepResult(_cleanupStep, false, failure);
        }
        catch (Exception ex)
        {
            return new DrillStepResult(_cleanupStep, false, $"The cleanup threw {ex.GetType().Name}; the drill agent may still be stopped.");
        }
    }

    private bool IsStopped(string agentId) => engine.GetStatus().AgentStops.Any(s => s.AgentId == agentId);
}
