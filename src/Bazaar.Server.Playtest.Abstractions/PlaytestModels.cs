using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Playtest.Abstractions;

/// <summary>Playtest tiers (docs/design/26-fun-systems.md section 7). Tier B (human capture) is not built here, so it has no member.</summary>
public enum PlaytestMode
{
    /// <summary>Autonomous headless scenarios; personas read state dumps, never pixels.</summary>
    TierA,
}

/// <summary>Engines with a headless path. Roblox Studio is Tier B only (doc 26 section 7), so it is absent.</summary>
public enum PlaytestEngine
{
    /// <summary>Godot <c>--headless</c> scenarios.</summary>
    Godot,

    /// <summary>Luau static checks.</summary>
    Luau,
}

/// <summary>One state assertion: the dump value at <paramref name="Key"/> must equal <paramref name="Expected"/>.</summary>
public sealed record PlaytestAssertion(string Id, string Key, string Expected);

/// <summary>A headless scenario to run against one build.</summary>
/// <param name="Engine">Which headless path runs it.</param>
/// <param name="BuildRef">The build to play (a commit or artifact reference).</param>
/// <param name="ScenarioId">Stable id of the scenario.</param>
/// <param name="Assertions">State assertions checked against the dump.</param>
/// <param name="Timeout">How long the host may run before the run is marked timed out.</param>
public sealed record PlaytestScenario(
    PlaytestEngine Engine,
    string BuildRef,
    string ScenarioId,
    IReadOnlyList<PlaytestAssertion> Assertions,
    TimeSpan Timeout);

/// <summary>
/// A state dump from a headless run: text key/value pairs, never pixels. <see cref="ContentHash"/> is canonical: it does not
/// depend on key order, so the same state always hashes the same.
/// </summary>
public sealed class StateDump
{
    public StateDump(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new SortedDictionary<string, string>(values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Lowercase hex SHA-256 over the sorted entries, each length-prefixed so no value can forge a boundary.</summary>
    public string ContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, value) in Values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{key.Length}:{key}={value.Length}:{value}\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed record AssertionResult(string AssertionId, bool Passed, string? Actual);

public enum PlaytestStatus
{
    Passed,
    AssertionsFailed,
    ProjectChanged,
    TimedOut,
    HostFailed,
    ProfileRejected,
}

/// <summary>The outcome of one run. <see cref="ReputationWeight"/> is always 0: notes weigh nothing until the owner accepts a card (doc 26 rule 2).</summary>
public sealed record PlaytestResult(
    Guid RunId,
    PlaytestScenario Scenario,
    PlaytestMode Mode,
    PlaytestStatus Status,
    string? StateDumpHash,
    IReadOnlyList<AssertionResult> Assertions,
    string? ProjectHashBefore,
    string? ProjectHashAfter,
    DateTimeOffset CreatedAt)
{
    public const int ReputationWeight = 0;
}

/// <summary>
/// What a headless host must run inside: a no-secrets VM profile with egress denied (docs 16 and 17, doc 26 section 7). The real VM
/// runner is a later work package; the runner refuses a host that does not declare both.
/// </summary>
public sealed record HostProfile(bool NoSecrets, bool EgressDenied)
{
    public static HostProfile Required { get; } = new(true, true);

    public bool Satisfies(HostProfile required)
    {
        ArgumentNullException.ThrowIfNull(required);
        return (!required.NoSecrets || NoSecrets) && (!required.EgressDenied || EgressDenied);
    }
}

/// <summary>
/// Hand-over to the tool-less reader session (doc 15). Structured data only: no tools, no credentials, no project files.
/// <see cref="HostedVisionAllowed"/> mirrors <c>playtest.hosted_vision</c>; Tier A never sends pixels either way.
/// </summary>
public sealed record PlaytestNoteRequest(
    Guid RunId,
    string ScenarioId,
    PlaytestStatus Status,
    IReadOnlyDictionary<string, string> StateDump,
    IReadOnlyList<AssertionResult> Assertions,
    bool HostedVisionAllowed);
