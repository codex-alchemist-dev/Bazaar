namespace Bazaar.Server.DevSandbox;

/// <summary>The operating systems a confinement provider can serve (doc 48 section 2).</summary>
public enum SandboxOs
{
    Windows,
    Linux,
}

/// <summary>The execution engine, chosen per module type (doc 48 section 2): WebAssembly for pure logic, a VM for native code.</summary>
public enum SandboxEngineKind
{
    Wasm,
    Vm,
}

/// <summary>What a module under test tried to do.</summary>
public enum SandboxActionKind
{
    Syscall,
    HostCall,
    FileAccess,
    NetworkAttempt,
    SecretRead,
    PermissionCheck,
}

/// <summary>The policy decision for one action.</summary>
public enum SandboxDecision
{
    Allowed,
    Denied,
}

/// <summary>The outcome of a run. Only <see cref="Clean"/> passes the Verified tag's monitored-sandbox check (doc 48 section 3).</summary>
public enum SandboxVerdict
{
    /// <summary>The run completed and every action was covered by an approved grant or the egress allowlist.</summary>
    Clean,

    /// <summary>At least one action had no approved grant. Fails the tag check.</summary>
    UndeclaredAccess,

    /// <summary>A canary value left the sandbox. Fails the tag check, high severity.</summary>
    CanaryTriggered,

    /// <summary>The runner refused to start (no tested OS provider, no engine, invalid spec, production target). Tag check stays pending.</summary>
    Refused,

    /// <summary>The run hit its wall-clock limit. Tag check stays pending.</summary>
    TimedOut,

    /// <summary>The run failed for a reason other than a policy violation. Tag check stays pending.</summary>
    Error,

    /// <summary>A replay of the audit stream produced a different verdict than the recorded one.</summary>
    ReplayMismatch,
}

/// <summary>
/// One approved permission, in the doc 48 section 1 vocabulary. <see cref="Class"/> is the permission class (for example
/// <c>net.egress</c> or <c>vault.read</c>); <see cref="Target"/> narrows it (a host, a path prefix, a key name), and
/// <c>*</c> means every target of that class.
/// </summary>
public sealed record SandboxPermissionGrant(string Class, string Target);

/// <summary>One action by the module under test, as reported by the engine or the confinement provider.</summary>
/// <param name="Kind">What kind of action.</param>
/// <param name="Class">The permission class the action needs (empty for a plain syscall that needs none).</param>
/// <param name="Target">The host, path, key name or call name the action touches.</param>
/// <param name="Payload">Text the action sends out (a request body, a written file's content). Scanned for canaries. May be empty.</param>
public sealed record SandboxAction(SandboxActionKind Kind, string Class, string Target, string Payload = "");

/// <summary>One entry in a run's hash-chained audit stream (doc 48 section 2.1 rule 7).</summary>
/// <param name="Sequence">Zero-based position in the stream.</param>
/// <param name="At">When it was recorded.</param>
/// <param name="Action">The action.</param>
/// <param name="Decision">The decision.</param>
/// <param name="Reason">Why (for example <c>no-grant</c>, <c>granted</c>, <c>egress-not-allowed</c>, <c>canary</c>).</param>
/// <param name="PreviousHash">Hex SHA-256 of the previous event; 64 zeros for the first.</param>
/// <param name="Hash">Hex SHA-256 over this event's fields and <paramref name="PreviousHash"/>.</param>
public sealed record SandboxAuditEvent(
    long Sequence,
    DateTimeOffset At,
    SandboxAction Action,
    SandboxDecision Decision,
    string Reason,
    string PreviousHash,
    string Hash);

/// <summary>Everything a run needs. Validated before any process starts.</summary>
/// <param name="RunId">Unique run id, lowercase letters, digits and hyphens only (it names the sandbox schema and role).</param>
/// <param name="ModuleId">The module under test.</param>
/// <param name="ModuleVersion">The module version under test (SemVer).</param>
/// <param name="Engine">The engine for this module type.</param>
/// <param name="Grants">The permissions the owner approved for this module.</param>
/// <param name="EgressAllowlist">Named test endpoints (host names) the run may reach. Empty blocks all egress.</param>
/// <param name="Seed">Seed for the fake-data company and canary values, so a run is reproducible.</param>
/// <param name="TimeoutSeconds">Wall-clock limit.</param>
/// <param name="MemoryLimitMb">Memory ceiling.</param>
/// <param name="TargetCompanyId">The company this run is aimed at, if any. Must be a sandbox company; a production id is refused.</param>
public sealed record SandboxRunSpec(
    string RunId,
    string ModuleId,
    string ModuleVersion,
    SandboxEngineKind Engine,
    IReadOnlyList<SandboxPermissionGrant> Grants,
    IReadOnlyList<string> EgressAllowlist,
    ulong Seed,
    int TimeoutSeconds = 300,
    int MemoryLimitMb = 512,
    string? TargetCompanyId = null);

/// <summary>The result of a run, stored with its audit stream as the evidence for the Verified tag's sandbox check.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Verdict">The verdict.</param>
/// <param name="Reason">A short human-readable reason for any verdict other than Clean.</param>
/// <param name="Violations">The audit events that were denied.</param>
/// <param name="EventCount">How many audit events the stream holds.</param>
/// <param name="AuditHeadHash">The last event's hash (64 zeros when the stream is empty).</param>
public sealed record SandboxRunResult(
    string RunId,
    SandboxVerdict Verdict,
    string Reason,
    IReadOnlyList<SandboxAuditEvent> Violations,
    long EventCount,
    string AuditHeadHash);

/// <summary>A confined module process, started by a provider. The engine feeds its actions to the runner through <see cref="ISandboxEngine"/>.</summary>
public interface ISandboxProcess : IAsyncDisposable
{
    /// <summary>Stops the process and everything it started. Idempotent.</summary>
    Task KillAsync(CancellationToken ct = default);
}

/// <summary>
/// OS confinement behind a provider interface (doc 03, doc 48 section 2). Linux: namespaces, seccomp, cgroups, Landlock. Windows: job
/// object, restricted token, AppContainer. A provider reports <see cref="Tested"/> only when its confinement has passed its own tests
/// on this OS; the runner refuses to use an untested one.
/// </summary>
public interface ISandboxConfinementProvider
{
    /// <summary>The OS this provider serves.</summary>
    SandboxOs Os { get; }

    /// <summary>True only when the confinement has been verified on this OS.</summary>
    bool Tested { get; }

    /// <summary>Starts a confined process for the spec.</summary>
    Task<ISandboxProcess> StartAsync(SandboxRunSpec spec, CancellationToken ct = default);
}

/// <summary>Runs the module in its engine and reports every action it attempts. Never decides policy itself.</summary>
public interface ISandboxEngine
{
    /// <summary>The engine kind this implementation serves.</summary>
    SandboxEngineKind Kind { get; }

    /// <summary>Runs the module in <paramref name="process"/> and yields each action as it happens, until the module ends or <paramref name="ct"/> is cancelled.</summary>
    IAsyncEnumerable<SandboxAction> RunAsync(SandboxRunSpec spec, ISandboxProcess process, SandboxTestCompany company, CancellationToken ct);
}

/// <summary>Builds the fake-data test company for a run (doc 48 section 2). Same seed gives the same company.</summary>
public interface ISandboxCompanyFactory
{
    /// <summary>Generates the company. Contains no real data.</summary>
    SandboxTestCompany Create(SandboxRunSpec spec);
}

/// <summary>The generated company: synthetic records plus the canary values planted in them.</summary>
/// <param name="CompanyId">A sandbox company id (prefix <c>sandbox-</c>).</param>
/// <param name="Schema">The separate database schema for this run.</param>
/// <param name="Role">The separate database role for this run.</param>
/// <param name="Records">Synthetic people, agents, GCs, DMs, repositories, Vault pages, mail and secrets, as name to JSON text.</param>
/// <param name="Canaries">The canary values planted in the records. Seeing one outside the sandbox is a violation.</param>
public sealed record SandboxTestCompany(
    string CompanyId,
    string Schema,
    string Role,
    IReadOnlyDictionary<string, string> Records,
    IReadOnlyList<string> Canaries);

/// <summary>Append-only, hash-chained record of a run (doc 48 section 2.1 rule 7).</summary>
public interface ISandboxAuditTrail
{
    /// <summary>The events so far, in order.</summary>
    IReadOnlyList<SandboxAuditEvent> Events { get; }

    /// <summary>Hash of the last event; 64 zeros when empty.</summary>
    string HeadHash { get; }

    /// <summary>Appends one event, chaining it to the previous.</summary>
    SandboxAuditEvent Append(SandboxAction action, SandboxDecision decision, string reason);

    /// <summary>True when every event's hash and link verify.</summary>
    bool Verify();
}

/// <summary>Runs one module under test end to end.</summary>
public interface ISandboxRunner
{
    /// <summary>Validates, gates, runs and judges one run. Never throws for a policy outcome; those are verdicts.</summary>
    Task<SandboxRunResult> RunAsync(SandboxRunSpec spec, CancellationToken ct = default);

    /// <summary>Replays a recorded stream against the spec's grants and allowlist and returns the verdict it yields.</summary>
    SandboxRunResult Replay(SandboxRunSpec spec, IReadOnlyList<SandboxAuditEvent> recorded);
}
