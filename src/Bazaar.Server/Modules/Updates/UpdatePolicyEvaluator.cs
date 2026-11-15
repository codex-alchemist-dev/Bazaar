using System.Globalization;

namespace Bazaar.Server.Modules.Updates;

/// <summary>
/// Decides what to do with one update candidate (doc 42 section 10.1). Pure apart from reading the clock through
/// <see cref="TimeProvider"/>: no I/O, no configuration, no OS calls.
/// </summary>
/// <remarks>
/// Rules run in this order and the first match wins:
/// <list type="number">
/// <item>A policy that pins or skips the candidate: <see cref="UpdateDecision.Pinned"/> or <see cref="UpdateDecision.Skipped"/>.</item>
/// <item>A candidate not newer than the installed version on its line: <see cref="UpdateDecision.Skipped"/>.</item>
/// <item>A failed test matrix: <see cref="UpdateDecision.Blocked"/>.</item>
/// <item>A GA version on a line not in use: <see cref="UpdateDecision.AwaitingApproval"/>. No policy can auto-adopt a new line.</item>
/// <item>The default decision (security patch on a line in use is Auto in a window, everything else Manual), replaced by the
/// most specific matching policy when there is one.</item>
/// <item>Beta and release candidates are never Auto, whatever the policy says.</item>
/// <item>An Auto decision is limited to a maintenance window. Outside one it becomes Deferred until the next window.</item>
/// </list>
/// Policies take precedence by scope: a module field outranks category, which outranks version type, which outranks channel.
/// The weights are powers of two, so a more specific field always outweighs every less specific field together. Two
/// policies of equal specificity both match: the one listed first wins.
/// </remarks>
public sealed class UpdatePolicyEvaluator
{
    private readonly TimeProvider _timeProvider;

    public UpdatePolicyEvaluator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>Evaluates one candidate against the policies and the installation context.</summary>
    public UpdateEvaluation Evaluate(UpdateCandidate candidate, UpdateContext context, IReadOnlyList<UpdatePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policies);

        var now = _timeProvider.GetUtcNow();
        var policy = FindMostSpecificPolicy(candidate, policies);

        if (policy?.Action == UpdatePolicyAction.Pin)
        {
            return new UpdateEvaluation(UpdateDecision.Pinned, UpdateRule.PinnedByPolicy, null, policy);
        }

        if (policy?.Action == UpdatePolicyAction.Skip)
        {
            return new UpdateEvaluation(UpdateDecision.Skipped, UpdateRule.SkippedByPolicy, null, policy);
        }

        var inUse = context.InstalledVersionsByLine.TryGetValue(candidate.Line, out var installedVersion);
        var change = inUse ? ClassifyChange(installedVersion!, candidate.Version) : UpdateChangeKind.Unknown;

        if (inUse && change == UpdateChangeKind.NotNewer)
        {
            return new UpdateEvaluation(UpdateDecision.Skipped, UpdateRule.NotNewer, null, policy);
        }

        if (!candidate.MatrixPassed)
        {
            return new UpdateEvaluation(UpdateDecision.Blocked, UpdateRule.MatrixFailed, null, policy);
        }

        var isGeneralAvailability = candidate.VersionType == VersionType.GeneralAvailability;
        if (!inUse && isGeneralAvailability)
        {
            return new UpdateEvaluation(UpdateDecision.AwaitingApproval, UpdateRule.NewLineNeedsApproval, null, policy);
        }

        // The default, then the policy override on top of it.
        var decision = UpdateDecision.Manual;
        var rule = UpdateRule.DefaultManual;
        var defaultSecurityPatch = inUse && candidate.IsSecurity && change == UpdateChangeKind.Patch;
        if (defaultSecurityPatch)
        {
            decision = UpdateDecision.Auto;
            rule = UpdateRule.SecurityPatchInWindow;
        }

        if (policy is not null)
        {
            (decision, rule) = policy.Action switch
            {
                UpdatePolicyAction.Auto => (UpdateDecision.Auto, UpdateRule.PolicyDecision),
                UpdatePolicyAction.Manual => (UpdateDecision.Manual, UpdateRule.PolicyDecision),
                UpdatePolicyAction.Defer => (UpdateDecision.Deferred, UpdateRule.PolicyDecision),
                UpdatePolicyAction.RequireApproval => (UpdateDecision.AwaitingApproval, UpdateRule.PolicyDecision),
                _ => (decision, rule),
            };
        }

        if (!isGeneralAvailability && decision == UpdateDecision.Auto)
        {
            return new UpdateEvaluation(UpdateDecision.Manual, UpdateRule.PreReleaseNotAuto, null, policy);
        }

        // Windows come from the policy when it has its own, otherwise from the module's defaults.
        var windows = policy is { Windows.Count: > 0 } ? policy.Windows : context.DefaultWindows;

        if (decision == UpdateDecision.Auto)
        {
            // The default security patch always needs a window, so with none configured it waits. A policy's own Auto with no
            // window configured is taken as the owner set it.
            if (windows.Count == 0)
            {
                return policy is null
                    ? new UpdateEvaluation(UpdateDecision.Deferred, UpdateRule.SecurityPatchDeferred, null, policy)
                    : new UpdateEvaluation(UpdateDecision.Auto, rule, null, policy);
            }

            if (MaintenanceWindowValidator.ContainsAny(windows, now))
            {
                return new UpdateEvaluation(UpdateDecision.Auto, rule, null, policy);
            }

            var deferRule = policy is null ? UpdateRule.SecurityPatchDeferred : UpdateRule.PolicyDecision;
            return new UpdateEvaluation(UpdateDecision.Deferred, deferRule, MaintenanceWindowValidator.NextStartAfter(windows, now), policy);
        }

        if (decision == UpdateDecision.Deferred)
        {
            return new UpdateEvaluation(UpdateDecision.Deferred, rule, MaintenanceWindowValidator.NextStartAfter(windows, now), policy);
        }

        return new UpdateEvaluation(decision, rule, null, policy);
    }

    /// <summary>The matching policy with the most specific scope, or null. The first listed wins a tie.</summary>
    public static UpdatePolicy? FindMostSpecificPolicy(UpdateCandidate candidate, IReadOnlyList<UpdatePolicy> policies)
    {
        UpdatePolicy? best = null;
        var bestWeight = -1;
        foreach (var policy in policies)
        {
            if (!Matches(policy, candidate))
            {
                continue;
            }

            var weight = ScopeWeight(policy.Scope);
            if (weight > bestWeight)
            {
                best = policy;
                bestWeight = weight;
            }
        }

        return best;
    }

    private static bool Matches(UpdatePolicy policy, UpdateCandidate candidate)
    {
        var scope = policy.Scope;
        var conditions = policy.Conditions;
        return (scope.ModuleId is null || string.Equals(scope.ModuleId, candidate.ModuleId, StringComparison.Ordinal))
            && (scope.Category is null || string.Equals(scope.Category, candidate.Category, StringComparison.Ordinal))
            && (scope.VersionType is null || scope.VersionType == candidate.VersionType)
            && (scope.Channel is null || string.Equals(scope.Channel, candidate.Channel, StringComparison.Ordinal))
            && (conditions.IsSecurity is null || conditions.IsSecurity == candidate.IsSecurity)
            && (conditions.MatrixPassed is null || conditions.MatrixPassed == candidate.MatrixPassed);
    }

    private static int ScopeWeight(UpdatePolicyScope scope) =>
        (scope.ModuleId is null ? 0 : 8)
        + (scope.Category is null ? 0 : 4)
        + (scope.VersionType is null ? 0 : 2)
        + (scope.Channel is null ? 0 : 1);

    /// <summary>How a candidate differs from the version installed on its line.</summary>
    private enum UpdateChangeKind
    {
        /// <summary>Equal to or older than the installed version.</summary>
        NotNewer,

        Patch,
        Minor,
        Major,

        /// <summary>The version text does not start with a numeric major.minor.patch, so no change kind is known.</summary>
        Unknown,
    }

    private static UpdateChangeKind ClassifyChange(string installed, string candidate)
    {
        if (!TryParseVersion(installed, out var current) || !TryParseVersion(candidate, out var next))
        {
            return UpdateChangeKind.Unknown;
        }

        if (next.CompareTo(current) <= 0)
        {
            return UpdateChangeKind.NotNewer;
        }

        if (next.Major != current.Major)
        {
            return UpdateChangeKind.Major;
        }

        return next.Minor != current.Minor ? UpdateChangeKind.Minor : UpdateChangeKind.Patch;
    }

    /// <summary>
    /// Reads the leading major.minor.patch numbers of a version. Missing parts count as zero, and any pre-release or build
    /// suffix after '-' or '+' is ignored.
    /// </summary>
    private static bool TryParseVersion(string text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        var core = text.Split(['-', '+'], 2)[0];
        var parts = core.Split('.');
        if (parts.Length > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = (numbers[0], numbers[1], numbers[2]);
        return true;
    }
}
