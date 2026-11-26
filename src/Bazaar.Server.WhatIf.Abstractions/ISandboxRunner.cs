namespace Bazaar.Server.WhatIf;

/// <summary>Egress policy of a sandbox. Only <see cref="Denied"/> is acceptable for What If (docs 15, 16).</summary>
public enum SandboxEgress
{
    Denied,
    Allowlisted,
    Open,
}

/// <summary>When the run may use the agents: BREAK and OFF windows by default, counted against quota (doc 26 section 3, doc 09).</summary>
public enum WorkLifeWindow
{
    Break,
    Off,
}

/// <summary>
/// The sandbox VM profile a branch runs in: no Passport, no GitHub App token, default-deny egress (docs 16, 17). The token fields exist
/// only so a caller that supplies one is rejected by <see cref="SandboxRequestGuard"/>; a valid profile leaves both null.
/// </summary>
public sealed record SandboxProfile(SandboxEgress Egress = SandboxEgress.Denied, string? PassportToken = null, string? GitHubAppToken = null)
{
    public static SandboxProfile Default { get; } = new();
}

/// <summary>One branch's task list to run in a sandbox. <see cref="PushRef"/> is where the host pushes the result; it must be under <c>sandbox/</c>.</summary>
public sealed record SandboxRequest(
    Guid RunId,
    string Branch,
    Guid SandboxGcId,
    IReadOnlyList<string> Tasks,
    string MetricsSpecHash,
    SandboxProfile Profile,
    string PushRef,
    WorkLifeWindow Window = WorkLifeWindow.Break);

/// <summary>What a runner reports. <see cref="MetricsSpecHash"/> is the hash it scored against; the engine aborts if it differs from the run's.</summary>
public sealed record SandboxOutcome(string MetricsSpecHash, BranchMeasurement Measurement, string PushedRef);

/// <summary>
/// Runs one branch in a sandbox VM. The real runner depends on docs 16 and 17 and is not built; <c>FakeSandboxRunner</c> stands in.
/// An implementation must call <see cref="SandboxRequestGuard.EnsureValid"/> itself, so it never trusts its caller.
/// </summary>
public interface ISandboxRunner
{
    Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct = default);
}

/// <summary>Thrown when a sandbox request breaks the profile rules.</summary>
public sealed class SandboxRequestRejectedException(IReadOnlyList<string> violations)
    : InvalidOperationException("Sandbox request rejected: " + string.Join("; ", violations))
{
    public IReadOnlyList<string> Violations { get; } = violations;
}

/// <summary>The rules a sandbox request must meet before it may reach a runner.</summary>
public static class SandboxRequestGuard
{
    public const string RefPrefix = "sandbox/";
    private const int _maxRefLength = 200;

    public static IReadOnlyList<string> Check(SandboxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        var profile = request.Profile;
        if (profile is null)
        {
            violations.Add("a sandbox profile is required");
            return violations;
        }

        if (!string.IsNullOrEmpty(profile.PassportToken))
            violations.Add("a Passport token must not enter a sandbox");
        if (!string.IsNullOrEmpty(profile.GitHubAppToken))
            violations.Add("a GitHub App token must not enter a sandbox");
        if (profile.Egress != SandboxEgress.Denied)
            violations.Add("egress must be default-deny");
        if (!IsSandboxRef(request.PushRef))
            violations.Add($"pushes may only target {RefPrefix}* refs");
        if (string.IsNullOrWhiteSpace(request.MetricsSpecHash))
            violations.Add("the metric spec hash is required");
        return violations;
    }

    public static void EnsureValid(SandboxRequest request)
    {
        var violations = Check(request);
        if (violations.Count > 0)
            throw new SandboxRequestRejectedException(violations);
    }

    /// <summary>True for a well-formed ref under <c>sandbox/</c>: no traversal, empty segments, control characters or git-reserved characters.</summary>
    public static bool IsSandboxRef(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > _maxRefLength || !value.StartsWith(RefPrefix, StringComparison.Ordinal))
            return false;
        if (value.Length == RefPrefix.Length || value.EndsWith('/') || value.EndsWith(".lock", StringComparison.Ordinal) || value.EndsWith('.'))
            return false;
        if (value.Contains("..", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal) || value.Contains("@{", StringComparison.Ordinal))
            return false;
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c)))
            return false;
        return value.Split('/').All(segment => segment.Length > 0 && !segment.StartsWith('.'));
    }
}
