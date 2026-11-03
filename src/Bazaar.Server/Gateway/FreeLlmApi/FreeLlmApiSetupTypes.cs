using System.Collections.Concurrent;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>The seven FreeLLMAPI guided-setup steps in docs/design/05-model-gateway.md, in order.</summary>
public enum FreeLlmApiSetupStep
{
    Connect = 1,
    Warnings = 2,
    Endpoint = 3,
    Probe = 4,
    Preset = 5,
    Routing = 6,
    SelfTest = 7,
}

/// <summary>Ok: the step passed. Failed: the step found a problem. Skipped: the step did not run or is waiting for operator input.</summary>
public enum FreeLlmApiStepStatus
{
    Ok,
    Failed,
    Skipped,
}

/// <summary>One step's outcome. The message is fixed-form text and never carries the key or an upstream body.</summary>
public sealed record FreeLlmApiStepResult(FreeLlmApiSetupStep Step, FreeLlmApiStepStatus Status, string Message);

/// <summary>The routing choice the operator makes in step 6.</summary>
public enum FreeLlmApiRoutingChoice
{
    /// <summary>Bazaar pins model ids itself. No dynamic route is created.</summary>
    Manual,

    /// <summary>Dynamic routing through the <c>custom/*</c> route.</summary>
    Custom,
}

public static class FreeLlmApiRoutingChoices
{
    /// <summary>The route that a <see cref="FreeLlmApiRoutingChoice.Custom"/> choice maps to.</summary>
    public const string CustomRouteName = "custom/*";

    /// <summary>The route name for a choice, or null for Manual (no route).</summary>
    public static string? RouteNameFor(FreeLlmApiRoutingChoice choice) =>
        choice == FreeLlmApiRoutingChoice.Custom ? CustomRouteName : null;
}

/// <summary>One warning the operator must acknowledge in step 2. Ids are stable keys for the acknowledgement store.</summary>
public sealed record FreeLlmApiWarning(string Id, string Text);

public static class FreeLlmApiWarnings
{
    public static IReadOnlyList<FreeLlmApiWarning> All { get; } =
    [
        new("single-user-scope", "FreeLLMAPI is for a single user on a trusted network, for personal experimentation. Its README and SECURITY.md say it is not for production."),
        new("free-tier-training", "Some free tiers train on prompts. Anything sent through a free tier may be used by that provider for training."),
        new("provider-terms", "Each upstream provider's terms are the operator's duty. Check them before you send data through a provider."),
        new("pin-version", "Pin the FreeLLMAPI version. Each upgrade is an operator action, and releases move quickly."),
        new("loopback-bind", "Keep FreeLLMAPI bound to 127.0.0.1 so that only this host can reach it."),
        new("backup-key", "Back up the FreeLLMAPI database and its ENCRYPTION_KEY together. Losing the key makes the stored provider keys unreadable."),
        new("no-subscription-credentials", "Do not route subscription logins (Claude Pro or Max, ChatGPT, Gemini CLI, Copilot) through FreeLLMAPI. Bazaar refuses them on every route."),
    ];
}

/// <summary>
/// Records warning acknowledgements with their timestamp. The first timestamp for an id is kept. TODO(component): the durable
/// implementation waits for the component model (PR #125, not merged); only <see cref="InMemoryWarningAcknowledgementStore"/> exists now.
/// </summary>
public interface IWarningAcknowledgementStore
{
    ValueTask<DateTimeOffset?> GetAsync(string warningId, CancellationToken cancellationToken);

    ValueTask RecordAsync(string warningId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken);
}

/// <summary>Process-lifetime acknowledgement store. Lost on restart, so the operator acknowledges again after a restart.</summary>
public sealed class InMemoryWarningAcknowledgementStore : IWarningAcknowledgementStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _acknowledged = new(StringComparer.Ordinal);

    public ValueTask<DateTimeOffset?> GetAsync(string warningId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_acknowledged.TryGetValue(warningId, out var at) ? (DateTimeOffset?)at : null);

    public ValueTask RecordAsync(string warningId, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken)
    {
        _acknowledged.TryAdd(warningId, acknowledgedAt);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The operator's input for one guided-setup run. Each run re-checks everything from the top. Ticks and choices are used for
/// this run only and are not stored. A null field means the operator has not supplied it yet.
/// </summary>
/// <param name="AcknowledgedWarningIds">Warnings ticked in this run. They are recorded with a timestamp.</param>
/// <param name="BaseUrl">The FreeLLMAPI base URL. Blank means <see cref="FreeLlmApiGuidedSetup.DefaultBaseUrl"/>.</param>
/// <param name="KeyReference">A secret reference (<c>env:NAME</c> or <c>file:PATH</c>), never the key.</param>
/// <param name="PresetModelIds">The free-lane models the operator ticked from the candidate list (step 5).</param>
/// <param name="Routing">Manual or Custom (step 6).</param>
public sealed record FreeLlmApiSetupRequest(
    IReadOnlyList<string>? AcknowledgedWarningIds = null,
    string? BaseUrl = null,
    string? KeyReference = null,
    IReadOnlyList<string>? PresetModelIds = null,
    FreeLlmApiRoutingChoice? Routing = null);

/// <summary>One HTTP call made during a run, with the exact URL. The key is never part of it.</summary>
public sealed record FreeLlmApiProbeCall(string Method, string Url, int? StatusCode, bool Succeeded);

/// <summary>
/// The result of one guided-setup run. <see cref="Steps"/> always holds all seven steps. <see cref="Candidates"/> is the
/// model list from the probe (step 5 input). <see cref="Violations"/> lists served models outside the expected set (step 7).
/// </summary>
public sealed record FreeLlmApiSetupReport(
    IReadOnlyList<FreeLlmApiStepResult> Steps,
    IReadOnlyList<FreeLlmApiProbeCall> Calls,
    IReadOnlyList<FreeLlmApiModel> Candidates,
    IReadOnlyList<string> Violations)
{
    /// <summary>True when every step is Ok.</summary>
    public bool Completed => Steps.All(step => step.Status == FreeLlmApiStepStatus.Ok);
}
