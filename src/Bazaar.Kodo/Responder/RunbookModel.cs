using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Responder;

/// <summary>The permission names of doc 46 section 14.5. The responder names a required control and nothing more; the role model (doc 48) decides who holds it.</summary>
public static class KodoControls
{
    public const string ViewStatus = "kodo.view.status";
    public const string ViewLogs = "kodo.view.logs";
    public const string ViewTraces = "kodo.view.traces";
    public const string ViewFleet = "kodo.view.fleet";
    public const string ViewAudit = "kodo.view.audit";
    public const string ControlRestart = "kodo.control.restart";
    public const string ControlIsolate = "kodo.control.isolate";
    public const string ControlRollback = "kodo.control.rollback";
    public const string RunbookRun = "kodo.runbook.run";
    public const string RunbookApprove = "kodo.runbook.approve";
    public const string IncidentEnter = "kodo.incident.enter";
    public const string IncidentExit = "kodo.incident.exit";
    public const string FailoverApprove = "kodo.failover.approve";
    public const string ChaosEnable = "kodo.chaos.enable";
    public const string WebhookManage = "kodo.webhook.manage";
    public const string Export = "kodo.export";
    public const string SettingsKodo = "kodo.settings.kodo";

    /// <summary>Every control name.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ViewStatus, ViewLogs, ViewTraces, ViewFleet, ViewAudit, ControlRestart, ControlIsolate, ControlRollback, RunbookRun,
        RunbookApprove, IncidentEnter, IncidentExit, FailoverApprove, ChaosEnable, WebhookManage, Export, SettingsKodo,
    };
}

/// <summary>The kinds of runbook step (doc 46 section 16.2).</summary>
public enum RunbookStepKind
{
    /// <summary>Reads state and changes nothing.</summary>
    Check,

    /// <summary>Changes something.</summary>
    Action,

    /// <summary>Reads state to confirm a fix worked. Changes nothing.</summary>
    Verify,

    /// <summary>Tells someone. Best effort.</summary>
    Notify,

    /// <summary>Lets time pass.</summary>
    Wait,
}

/// <summary>How risky a runbook is. High always needs approval and step-up.</summary>
public enum RunbookRisk
{
    Low,
    Medium,
    High,
}

/// <summary>The least approval a runbook accepts (section 16.5). An operator setting may tighten it, never loosen it.</summary>
public enum RunbookApprovalTier
{
    /// <summary>No approval is needed when the run is eligible to be unattended.</summary>
    None,

    /// <summary>An explicit approval for each action step.</summary>
    Approval,

    /// <summary>An explicit approval with step-up for each action step.</summary>
    ApprovalStepUp,
}

/// <summary>One declarative step. Parameters are identifiers only.</summary>
/// <param name="Id">The step id, unique inside its list.</param>
/// <param name="Kind">The step kind.</param>
/// <param name="Operation">The operation name the runner understands, as a component id.</param>
/// <param name="Parameters">Identifier parameters the runner needs.</param>
/// <param name="Timeout">How long the runner may take.</param>
/// <param name="MaxAttempts">The attempts this step asks for. The engine caps it.</param>
/// <param name="RequiresApproval">Gate this step even when the run would otherwise be unattended.</param>
/// <param name="WaitFor">For a wait step, how long to wait. Null otherwise.</param>
public sealed record RunbookStep(
    string Id,
    RunbookStepKind Kind,
    string Operation,
    IReadOnlyDictionary<string, string> Parameters,
    TimeSpan Timeout,
    int MaxAttempts = 1,
    bool RequiresApproval = false,
    TimeSpan? WaitFor = null);

/// <summary>
/// A runbook definition (section 16.2): id, version, preconditions, steps (with their verify step), rollback, required control, risk,
/// approval tier and estimated duration. Its canonical bytes are what a signature covers.
/// </summary>
/// <param name="Id">The runbook id, a component id.</param>
/// <param name="Version">The version, 1 or more. A version is immutable once in a catalogue.</param>
/// <param name="Title">A short title for the preview.</param>
/// <param name="Risk">The risk class.</param>
/// <param name="ApprovalTier">The least approval it accepts.</param>
/// <param name="RequiredControl">The control name a starter must hold (section 14.5).</param>
/// <param name="EstimatedDuration">An estimate for the preview.</param>
/// <param name="Preconditions">Check steps that must pass before anything changes.</param>
/// <param name="Steps">The forward steps, including at least one verify step when any action exists.</param>
/// <param name="Rollback">The steps that undo the actions, in order.</param>
public sealed record RunbookDefinition(
    string Id,
    int Version,
    string Title,
    RunbookRisk Risk,
    RunbookApprovalTier ApprovalTier,
    string RequiredControl,
    TimeSpan EstimatedDuration,
    IReadOnlyList<RunbookStep> Preconditions,
    IReadOnlyList<RunbookStep> Steps,
    IReadOnlyList<RunbookStep> Rollback)
{
    /// <summary>The most steps in any one list.</summary>
    public const int MaxStepsPerList = 32;

    /// <summary>The longest title.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>The longest single step timeout.</summary>
    public static TimeSpan MaxStepTimeout { get; } = TimeSpan.FromHours(1);

    /// <summary>The first problem found, as a fixed code, or null when the definition is acceptable.</summary>
    public string? Problem()
    {
        if (string.IsNullOrEmpty(Id) || !KodoExportPatterns.ComponentId().IsMatch(Id) || Id.Length > 64)
        {
            return "invalid_id";
        }

        if (Version < 1 || !Enum.IsDefined(Risk) || !Enum.IsDefined(ApprovalTier))
        {
            return "invalid_header";
        }

        if (string.IsNullOrWhiteSpace(Title) || Title.Length > MaxTitleLength || !KodoControls.All.Contains(RequiredControl ?? string.Empty))
        {
            return "invalid_header";
        }

        if (EstimatedDuration < TimeSpan.Zero || Preconditions is null || Steps is null || Rollback is null || Steps.Count == 0)
        {
            return "invalid_header";
        }

        if (Risk == RunbookRisk.High && ApprovalTier != RunbookApprovalTier.ApprovalStepUp)
        {
            return "high_risk_needs_step_up";
        }

        foreach ((string name, IReadOnlyList<RunbookStep> list) in new[] { ("pre", Preconditions), ("step", Steps), ("rollback", Rollback) })
        {
            if (list.Count > MaxStepsPerList)
            {
                return "too_many_steps";
            }

            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (RunbookStep step in list)
            {
                string? stepProblem = ProblemOf(step, name);
                if (stepProblem is not null || !ids.Add(step.Id))
                {
                    return stepProblem ?? "duplicate_step_id";
                }
            }
        }

        if (Steps.Any(step => step.Kind == RunbookStepKind.Action) && !Steps.Any(step => step.Kind == RunbookStepKind.Verify))
        {
            return "missing_verify_step";
        }

        return null;
    }

    private static string? ProblemOf(RunbookStep step, string list)
    {
        if (step is null || string.IsNullOrEmpty(step.Id) || !KodoExportPatterns.EventId().IsMatch(step.Id)
            || string.IsNullOrEmpty(step.Operation) || !KodoExportPatterns.ComponentId().IsMatch(step.Operation)
            || !Enum.IsDefined(step.Kind) || step.Parameters is null || step.Parameters.Count > 16)
        {
            return "invalid_step";
        }

        if (step.Timeout <= TimeSpan.Zero || step.Timeout > MaxStepTimeout || step.MaxAttempts is < 1 or > 5)
        {
            return "invalid_step";
        }

        foreach (KeyValuePair<string, string> pair in step.Parameters)
        {
            if (pair.Key is null || pair.Value is null
                || !KodoExportPatterns.IdentifierName().IsMatch(pair.Key) || !KodoExportPatterns.IdentifierValue().IsMatch(pair.Value))
            {
                return "invalid_step";
            }
        }

        bool hasWait = step.WaitFor is { } wait && wait > TimeSpan.Zero && wait <= TimeSpan.FromHours(24);
        if ((step.Kind == RunbookStepKind.Wait) != hasWait)
        {
            return "invalid_step";
        }

        if (list == "pre" && step.Kind != RunbookStepKind.Check)
        {
            return "precondition_not_check";
        }

        if (list == "rollback" && step.Kind is RunbookStepKind.Wait)
        {
            return "invalid_step";
        }

        return null;
    }
}

/// <summary>The one canonical encoding of a definition, and its digest. A signature covers these bytes.</summary>
public static class RunbookCanonical
{
    /// <summary>The canonical UTF-8 bytes: fixed key order, parameters sorted by name, no insignificant whitespace.</summary>
    public static byte[] Bytes(RunbookDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartObject();
            w.WriteString("id", definition.Id);
            w.WriteNumber("version", definition.Version);
            w.WriteString("title", definition.Title);
            w.WriteString("risk", definition.Risk.ToString());
            w.WriteString("approvalTier", definition.ApprovalTier.ToString());
            w.WriteString("requiredControl", definition.RequiredControl);
            w.WriteNumber("estimatedSeconds", (long)definition.EstimatedDuration.TotalSeconds);
            WriteList(w, "preconditions", definition.Preconditions);
            WriteList(w, "steps", definition.Steps);
            WriteList(w, "rollback", definition.Rollback);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The lowercase hex SHA-256 of <see cref="Bytes"/>.</summary>
    public static string Digest(RunbookDefinition definition) => Convert.ToHexStringLower(SHA256.HashData(Bytes(definition)));

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<RunbookStep> steps)
    {
        w.WriteStartArray(name);
        foreach (RunbookStep step in steps)
        {
            w.WriteStartObject();
            w.WriteString("id", step.Id);
            w.WriteString("kind", step.Kind.ToString());
            w.WriteString("operation", step.Operation);
            w.WriteStartObject("parameters");
            foreach (KeyValuePair<string, string> pair in step.Parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                w.WriteString(pair.Key, pair.Value);
            }

            w.WriteEndObject();
            w.WriteNumber("timeoutMs", (long)step.Timeout.TotalMilliseconds);
            w.WriteNumber("maxAttempts", step.MaxAttempts);
            w.WriteBoolean("requiresApproval", step.RequiresApproval);
            w.WriteNumber("waitMs", (long)(step.WaitFor?.TotalMilliseconds ?? 0));
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}

/// <summary>A runbook the catalogue accepted.</summary>
/// <param name="Definition">The definition.</param>
/// <param name="Digest">Its canonical digest.</param>
public sealed record CatalogueEntry(RunbookDefinition Definition, string Digest);

/// <summary>
/// The set of runbooks the engine will run. A runbook enters only with a valid signature over its canonical bytes (section 16.2), so an
/// unknown or altered runbook is refused. A version is immutable: the same id and version never holds two digests.
/// </summary>
public sealed class RunbookCatalogue
{
    private readonly IRuntimeSignatureVerifier _verifier;
    private readonly byte[] _publicKey;
    private readonly Dictionary<(string, int), CatalogueEntry> _entries = [];

    /// <summary>Creates a catalogue that trusts one signing key.</summary>
    public RunbookCatalogue(IRuntimeSignatureVerifier verifier, ReadOnlySpan<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
        _publicKey = publicKey.ToArray();
    }

    /// <summary>How many runbooks it holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Adds a signed runbook. Returns null when accepted, otherwise the refusal code.</summary>
    public string? TryAdd(RunbookDefinition definition, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string? problem = definition.Problem();
        if (problem is not null)
        {
            return problem;
        }

        byte[] bytes = RunbookCanonical.Bytes(definition);
        if (!_verifier.Verify(bytes, signature, _publicKey))
        {
            return "bad_signature";
        }

        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (_entries.TryGetValue((definition.Id, definition.Version), out CatalogueEntry? existing))
        {
            return existing.Digest == digest ? null : "version_conflict";
        }

        _entries[(definition.Id, definition.Version)] = new CatalogueEntry(definition, digest);
        return null;
    }

    /// <summary>Finds a runbook by id and version.</summary>
    public bool TryGet(string id, int version, out CatalogueEntry? entry) => _entries.TryGetValue((id, version), out entry);

    /// <summary>The highest version of a runbook, or null.</summary>
    public CatalogueEntry? Latest(string id) =>
        _entries.Where(pair => pair.Key.Item1 == id).OrderByDescending(pair => pair.Key.Item2).Select(pair => pair.Value).FirstOrDefault();
}
