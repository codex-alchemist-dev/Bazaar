using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Setup;

/// <summary>
/// Applies a setup plan under the instance lock (doc 45 sections 1.2, 1.3 and 1.7). The order is fixed: check the plan hash,
/// validate (errors refuse with 1, holds with 2), take the lock (held is 2), run the steps in order, and write the audit rows.
/// A failing step rolls back the steps already applied, in reverse order, and the apply returns 3. A refused or held apply writes
/// no audit row, because nothing changed.
/// </summary>
/// <remarks>
/// Audit: one <c>setup.plan.applied</c> row through <see cref="IAuditLog"/>, then one <c>setting.changed</c> row per changed
/// settings key through <see cref="ISettingAuditSink"/>. Secret references are logged by field name and family only, and a
/// step's exception is logged by type only, because exception text can carry a value.
/// </remarks>
public sealed class SetupApplier
{
    /// <summary>The audit kind of the apply row (doc 45 section 1.2).</summary>
    public const string AppliedKind = "setup.plan.applied";

    private const string _settingsPrefix = "settings.";

    private readonly ISetupInstanceLock _instanceLock;
    private readonly List<ISetupStep> _steps;
    /// <summary>The most audit counts one step may add to its row.</summary>
    private const int _maxCountsPerStep = 32;

    private readonly IAuditLog _audit;
    private readonly ISettingAuditSink _settingAudit;
    private readonly TimeProvider _time;
    private readonly SettingRegistry _registry;

    /// <param name="steps">The steps to run. They run in <see cref="ISetupStep.Order"/>, ties broken by <see cref="ISetupStep.Id"/>.</param>
    /// <param name="registry">The settings registry the plan is validated against. Defaults to the registry this build ships.</param>
    public SetupApplier(
        ISetupInstanceLock instanceLock,
        IEnumerable<ISetupStep> steps,
        IAuditLog audit,
        ISettingAuditSink settingAudit,
        TimeProvider time,
        SettingRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(instanceLock);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(settingAudit);
        ArgumentNullException.ThrowIfNull(time);

        _steps = steps.OrderBy(step => step.Order).ThenBy(step => step.Id, StringComparer.Ordinal).ToList();
        var duplicate = _steps.GroupBy(step => step.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two setup steps share the id '{duplicate.Key}'.", nameof(steps));
        }

        _instanceLock = instanceLock;
        _audit = audit;
        _settingAudit = settingAudit;
        _time = time;
        _registry = registry ?? SettingRegistry.Known;
    }

    /// <summary>Applies <paramref name="request"/>. Never throws for a refused, held or failed apply; only for a broken caller or an audit write that fails.</summary>
    public async Task<SetupApplyResult> ApplyAsync(SetupApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperatorIdentity);
        ArgumentNullException.ThrowIfNull(request.PlanHash);

        // a. The hash comes first. A stale dry run is refused before anything else is looked at, and nothing is written.
        var hash = SetupPlanCanonicalizer.Hash(request.Plan);
        if (!string.Equals(hash, request.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return new SetupApplyResult(
                SetupExitCode.InvalidInput,
                hash,
                [],
                [new SetupValidationIssue(SetupIssueSeverity.Error, "planHash", "does not match the plan; run the dry run again and apply the hash it prints.")]);
        }

        // b. Validation normalizes the plan in place, as the loader does. Errors refuse with 1, holds with 2. Held plans never apply.
        var issues = new List<SetupValidationIssue>();
        SetupPlanValidator.Validate(request.Plan, _registry, issues);
        var validation = SetupExitCodes.ForIssues(issues);
        if (validation != SetupExitCode.Ok)
        {
            return new SetupApplyResult(validation, hash, [], issues);
        }

        var state = request.CurrentState ?? SetupStateSnapshot.Empty;
        var changes = SetupChangePlanner.Compute(request.Plan, state).Changes;

        // c. The instance lock. Held elsewhere: nothing changes and nothing is written.
        IAsyncDisposable lease;
        try
        {
            lease = await _instanceLock.AcquireAsync(cancellationToken);
        }
        catch (SetupInstanceLockHeldException)
        {
            return new SetupApplyResult(
                SetupExitCode.Held,
                hash,
                [],
                [new SetupValidationIssue(SetupIssueSeverity.Hold, "instance", "another process holds the instance lock; nothing was changed. Run apply again when it is released.")]);
        }

        await using (lease)
        {
            return await ApplyUnderLockAsync(request, hash, state, changes, cancellationToken);
        }
    }

    private async Task<SetupApplyResult> ApplyUnderLockAsync(
        SetupApplyRequest request,
        string hash,
        SetupStateSnapshot state,
        IReadOnlyList<SetupChange> changes,
        CancellationToken cancellationToken)
    {
        var context = new SetupApplyContext(
            hash,
            request.EntryPath,
            request.OperatorIdentity,
            request.TemplateId,
            request.TemplateVersion,
            state,
            changes);

        // With no step registered, nothing can apply a change. Fail before any step runs, so there is nothing to roll back.
        if (_steps.Count == 0 && changes.Count > 0)
        {
            var path = changes[0].Path;
            return await FailAsync(
                request,
                hash,
                context,
                [],
                null,
                "no_step_implementation",
                new SetupValidationIssue(SetupIssueSeverity.Error, path, $"no step implementation for {path}"));
        }

        // d. Steps in order. The first failure (a report or an exception) stops the run and rolls back what completed.
        var applied = new List<ISetupStep>();
        var stepCounts = new List<JsonObject>();
        foreach (var step in _steps)
        {
            string? failureKind = null;
            try
            {
                var result = await step.ApplyAsync(request.Plan, context, cancellationToken);
                if (!result.Succeeded)
                {
                    failureKind = "reported_failure";
                }
                else
                {
                    stepCounts.AddRange(CountRows(step.Id, result.AuditCounts));
                }
            }
            catch (Exception exception)
            {
                // The exception text can carry a secret, so only its type is kept.
                failureKind = exception.GetType().Name;
            }

            if (failureKind is not null)
            {
                return await FailAsync(
                    request,
                    hash,
                    context,
                    applied,
                    step.Id,
                    failureKind,
                    new SetupValidationIssue(SetupIssueSeverity.Error, step.Id, $"step failed ({failureKind}); the applied steps were rolled back."));
            }

            applied.Add(step);
        }

        // e. Success: one setup.plan.applied row, then one setting.changed row per changed settings key.
        var appliedIds = applied.Select(step => step.Id).ToList();
        var changedKeys = ChangedSettingKeys(request.Plan, changes);
        await AppendAppliedRowAsync(request, hash, "applied", changedKeys, appliedIds, null, null, [], cancellationToken, stepCounts);

        foreach (var key in changedKeys)
        {
            var oldValue = state.LeafValues.TryGetValue(_settingsPrefix + key, out var current) ? current : null;
            var newValue = request.Plan.Settings.TryGetValue(key, out var element)
                ? SetupPlanCanonicalizer.Text(JsonNode.Parse(element.GetRawText()))
                : "null";
            await _settingAudit.AppendAsync(
                new SettingChangeAudit(
                    key,
                    SettingScope.Install,
                    string.Empty,
                    request.OperatorIdentity,
                    SettingWriteOutcome.Accepted,
                    oldValue,
                    newValue,
                    null),
                cancellationToken);
        }

        // f. The result. Only step ids and issues leave here, never a step's message.
        return new SetupApplyResult(SetupExitCode.Ok, hash, appliedIds, []);
    }

    /// <summary>
    /// Rolls back <paramref name="applied"/> in reverse order, writes the failure row and returns exit 3. Every rollback is
    /// attempted, even after one fails. Rollback runs with no cancellation, so it always finishes.
    /// </summary>
    private async Task<SetupApplyResult> FailAsync(
        SetupApplyRequest request,
        string hash,
        SetupApplyContext context,
        List<ISetupStep> applied,
        string? failedStepId,
        string failureKind,
        SetupValidationIssue failure)
    {
        var issues = new List<SetupValidationIssue> { failure };
        var rollbackFailures = new List<string>();

        for (var index = applied.Count - 1; index >= 0; index--)
        {
            var step = applied[index];
            try
            {
                await step.RollbackAsync(request.Plan, context, CancellationToken.None);
            }
            catch (Exception exception)
            {
                rollbackFailures.Add($"{step.Id}:{exception.GetType().Name}");
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, step.Id, "rollback failed; check the audit row and repair this step by hand."));
            }
        }

        var outcome = rollbackFailures.Count == 0 ? "rolled_back" : "rollback_failed";
        await AppendAppliedRowAsync(request, hash, outcome, [], [], failedStepId, failureKind, rollbackFailures, CancellationToken.None);
        return new SetupApplyResult(SetupExitCode.ApplyFailedRolledBack, hash, [], issues);
    }

    /// <summary>The settings keys the change list touches, sorted. A key is the plan's settings key, or a registered key for a removal.</summary>
    private IReadOnlyList<string> ChangedSettingKeys(SetupPlan plan, IReadOnlyList<SetupChange> changes)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!change.Path.StartsWith(_settingsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = change.Path[_settingsPrefix.Length..];
            var key = (plan.Settings.ContainsKey(rest) || _registry.TryGet(rest, out _))
                ? rest
                : plan.Settings.Keys.FirstOrDefault(known => rest.StartsWith(known + ".", StringComparison.Ordinal));
            if (key is not null)
            {
                keys.Add(key);
            }
        }

        return [.. keys];
    }

    private Task AppendAppliedRowAsync(
        SetupApplyRequest request,
        string hash,
        string outcome,
        IReadOnlyList<string> changedKeys,
        IReadOnlyList<string> appliedIds,
        string? failedStepId,
        string? failureKind,
        IReadOnlyList<string> rollbackFailures,
        CancellationToken cancellationToken,
        IReadOnlyList<JsonObject>? stepCounts = null)
    {
        var payload = new JsonObject
        {
            ["planHash"] = hash,
            ["entryPath"] = WireName(request.EntryPath),
            ["operator"] = request.OperatorIdentity,
            ["templateId"] = request.TemplateId,
            ["templateVersion"] = request.TemplateVersion,
            // Kodo verification is a later slice, so the result is the literal null for now (doc 45 section 1.12).
            ["kodoResult"] = null,
            ["outcome"] = outcome,
            ["changedSettingKeys"] = StringArray(changedKeys),
            ["appliedSteps"] = StringArray(appliedIds),
            ["failedStep"] = failedStepId,
            ["failureKind"] = failureKind,
            ["rollbackFailures"] = StringArray(rollbackFailures),
            ["secretReferences"] = SecretReferenceArray(request.Plan),
        };

        // Only present when a step reported counts, so a row without them keeps its earlier shape.
        if (stepCounts is { Count: > 0 })
        {
            var rows = new JsonArray();
            foreach (var row in stepCounts)
            {
                rows.Add((JsonNode)row);
            }

            payload["stepCounts"] = rows;
        }

        var entry = new AuditEntry(_time.GetUtcNow(), request.OperatorIdentity, AppliedKind, payload.ToJsonString());
        return _audit.AppendAsync(entry, cancellationToken);
    }

    /// <summary>
    /// One row per valid count: step id, name, value. The name is data, not a key, so the audit payload guard (which refuses
    /// secret-like keys) is not tripped by names such as <c>pass</c>. Sorted by name; invalid or surplus entries are dropped.
    /// </summary>
    private static IEnumerable<JsonObject> CountRows(string stepId, IReadOnlyDictionary<string, int> counts)
    {
        return counts
            .Where(pair => pair.Value >= 0 && IsCountName(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_maxCountsPerStep)
            .Select(pair => new JsonObject { ["step"] = stepId, ["name"] = pair.Key, ["count"] = pair.Value });
    }

    private static bool IsCountName(string? name) =>
        name is { Length: >= 1 and <= 64 } && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            JsonNode item = JsonValue.Create(value)!;
            array.Add(item);
        }

        return array;
    }

    /// <summary>Each secret reference by field name and family. The reference text is never written, and nothing is resolved here.</summary>
    private static JsonArray SecretReferenceArray(SetupPlan plan)
    {
        var references = new JsonArray();
        AddReference(references, "custody.kekRef", plan.Custody.KekRef);
        if (plan.Custody.MergeCredential.AppIdRef is { } appIdRef)
        {
            AddReference(references, "custody.mergeCredential.appIdRef", appIdRef);
        }

        for (var index = 0; index < plan.Models.Sources.Count; index++)
        {
            if (plan.Models.Sources[index].KeyRef is { } keyRef)
            {
                AddReference(references, $"models.sources[{index}].keyRef", keyRef);
            }
        }

        return references;
    }

    private static void AddReference(JsonArray references, string name, string text)
    {
        JsonNode entry = new JsonObject
        {
            ["name"] = name,
            ["family"] = SecretReferences.Classify(text).ToString().ToLowerInvariant(),
            ["status"] = "unresolved",
        };
        references.Add(entry);
    }

    private static string WireName(SetupEntryPath path) => path switch
    {
        SetupEntryPath.Wizard => "wizard",
        SetupEntryPath.Cli => "cli",
        SetupEntryPath.File => "file",
        SetupEntryPath.Api => "api",
        SetupEntryPath.Env => "env",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown entry path."),
    };
}
