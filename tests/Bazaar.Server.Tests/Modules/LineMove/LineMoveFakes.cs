using Bazaar.Server.Modules.LineMove;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.LineMove;

/// <summary>A scripted step executor. Records every call in a shared list so tests can check the run order.</summary>
internal sealed class FakeExecutor(LineMoveStep step, List<LineMoveStep> ran, Func<LineMoveContext, CancellationToken, Task<StepResult>>? behaviour = null)
    : ILineMoveStepExecutor
{
    public LineMoveStep ForStep { get; } = step;

    public List<LineMoveContext> Contexts { get; } = [];

    public Task<StepResult> ExecuteAsync(LineMoveContext context, CancellationToken cancellationToken)
    {
        ran.Add(ForStep);
        Contexts.Add(context);
        return behaviour is null
            ? Task.FromResult(Default(ForStep))
            : behaviour(context, cancellationToken);
    }

    private static StepResult Default(LineMoveStep step) => step == LineMoveStep.Snapshot
        ? StepResult.Success("Snapshot taken.", snapshotId: "snap-1")
        : StepResult.Success($"{step} passed.");
}

/// <summary>An in-memory audit sink. Keeps every event in order.</summary>
internal sealed class InMemoryAuditSink : IAuditSink
{
    public List<LineMoveAuditEvent> Events { get; } = [];

    public ValueTask RecordAsync(LineMoveAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Builds a full executor set for a run. Each step has a default pass; tests override a step with
/// <see cref="Script"/> to fail it, cancel it, or inspect its context.
/// </summary>
internal sealed class FakeSteps
{
    private readonly Dictionary<LineMoveStep, Func<LineMoveContext, CancellationToken, Task<StepResult>>> _behaviours = [];

    public List<LineMoveStep> Ran { get; } = [];

    public Dictionary<LineMoveStep, FakeExecutor> Executors { get; } = [];

    public FakeSteps Script(LineMoveStep step, Func<LineMoveContext, CancellationToken, Task<StepResult>> behaviour)
    {
        _behaviours[step] = behaviour;
        return this;
    }

    public FakeSteps Fail(LineMoveStep step, string reason) =>
        Script(step, (_, _) => Task.FromResult(StepResult.Failure(reason)));

    public IEnumerable<ILineMoveStepExecutor> Build()
    {
        foreach (var step in new[]
                 {
                     LineMoveStep.Snapshot, LineMoveStep.DryRun, LineMoveStep.Switch,
                     LineMoveStep.Verify, LineMoveStep.Complete, LineMoveStep.RolledBack,
                 })
        {
            _behaviours.TryGetValue(step, out var behaviour);
            var executor = new FakeExecutor(step, Ran, behaviour);
            Executors[step] = executor;
            yield return executor;
        }
    }
}

internal static class LineMoveTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Clock() => new(Now);

    /// <summary>Healthy preflight facts: every rule passes.</summary>
    public static LineMovePreflightInputs HealthyPreflight() => new(
        Extensions: [new InstalledExtension("pgvector", HasTargetLineArtifact: true)],
        FreeDiskBytes: 100,
        SnapshotBytes: 30,
        UpgradeBytes: 50,
        KodoGreenOnCurrentLine: true,
        RestoreDrillAge: TimeSpan.FromDays(2),
        MaxRestoreDrillAge: TimeSpan.FromDays(7));

    public static LineMoveRequest Request(bool windowOpen = true, LineMovePreflightInputs? preflight = null) => new(
        MoveId: "move-1",
        FromLine: 18,
        ToLine: 19,
        ApprovingRole: "owner",
        RollbackWindowOpen: windowOpen,
        Preflight: preflight ?? HealthyPreflight());
}
