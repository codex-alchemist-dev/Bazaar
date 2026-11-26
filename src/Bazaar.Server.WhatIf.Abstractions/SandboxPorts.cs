namespace Bazaar.Server.WhatIf;

/// <summary>
/// What the host needs to build one sandbox VM for one branch. It has no field for a Passport token, a GitHub App token or any other
/// secret, so no secret can be passed by construction (doc 26 section 3; docs 15, 16, 17 and DECISIONS-LOG C05: tokens never enter a VM).
/// </summary>
/// <remarks>
/// Maps to doc 16 "Build design" (WP7 to WP9): a no-Passport profile on the per-agent VM images (C22), no <c>scoped_mount</c> for the
/// Soul or Passport folders, and <c>IEgressPolicy.BlockAllAsync</c> (default-deny). The host adapter turns this into the
/// <c>Bazaar.Server.Vms</c> environment and VM lifecycle calls (<c>IVmBackend</c>).
/// </remarks>
/// <param name="RunId">The What If run, for audit and idempotent provisioning.</param>
/// <param name="Branch">The branch name inside the run.</param>
/// <param name="SandboxGcId">The <c>sandbox</c> GC the branch works in.</param>
/// <param name="Egress">Always <see cref="SandboxEgress.Denied"/>; the runner never passes anything else.</param>
public sealed record SandboxVmSpec(Guid RunId, string Branch, Guid SandboxGcId, SandboxEgress Egress);

/// <summary>How one task ended inside the sandbox.</summary>
/// <param name="Completed">False when the harness could not finish the task (not the same as CI failing).</param>
/// <param name="FailureCode">A short, secret-free code when <paramref name="Completed"/> is false.</param>
public sealed record SandboxTaskResult(bool Completed, string? FailureCode = null);

/// <summary>A push request for the host-side push gate. It carries a ref and ids, never a credential.</summary>
/// <param name="RunId">The What If run.</param>
/// <param name="Branch">The branch name inside the run.</param>
/// <param name="SessionId">The sandbox session whose work is pushed.</param>
/// <param name="Ref">The target ref; must be under <c>sandbox/</c>.</param>
public sealed record SandboxPushRequest(Guid RunId, string Branch, string SessionId, string Ref);

/// <summary>Why a sandbox start was refused by the quota port.</summary>
public enum SandboxQuotaRefusal
{
    None,

    /// <summary>The Work-Life state is not an allowed window (BREAK or OFF by default).</summary>
    OutsideWindow,

    /// <summary>The quota for fun activity is used up (doc 09, doc 26 rule 4).</summary>
    QuotaExhausted,
}

/// <summary>
/// The quota port's answer. A refusal carries a secret-free reason; a grant carries the id the refund names, so a refund is
/// idempotent per grant.
/// </summary>
public sealed record SandboxQuotaDecision(bool Granted, SandboxQuotaRefusal Refusal = SandboxQuotaRefusal.None, Guid GrantId = default)
{
    public static SandboxQuotaDecision Grant(Guid grantId) => new(true, SandboxQuotaRefusal.None, grantId);

    public static SandboxQuotaDecision Refuse(SandboxQuotaRefusal refusal) => new(false, refusal);
}

/// <summary>A start request to the quota port: one branch in one window.</summary>
public sealed record SandboxQuotaRequest(Guid RunId, string Branch, WorkLifeWindow Window);

/// <summary>
/// Work-Life gate and quota (doc 26 section 3: "BREAK and OFF windows by default, counted against quota"). A grant consumes quota.
/// </summary>
/// <remarks>Maps to the Work-Life availability state and quota ledger of doc 09 (<c>Bazaar.Server.WorkLife</c>); no type is referenced yet.</remarks>
public interface ISandboxQuota
{
    Task<SandboxQuotaDecision> TryConsumeAsync(SandboxQuotaRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the quota a grant consumed. Idempotent per grant: true when this call refunded it, false when it was already refunded
    /// or is unknown. The runner calls it when infrastructure fails (provision, collect, push, teardown), never when the task failed
    /// on its merits (PROPOSED, doc 26 gap decisions).
    /// </summary>
    Task<bool> RefundAsync(Guid grantId, CancellationToken ct = default);
}

/// <summary>
/// One provisioned sandbox VM. <see cref="TeardownAsync"/> must be idempotent: the runner calls it once in a finally block, and a
/// host that already lost the VM treats it as done.
/// </summary>
/// <remarks>
/// Maps to doc 17 <c>IWorkspace</c> / <c>ICodingGitEngine</c> (worktree, commit with trailers, <c>DiffAgainstBaseAsync</c>), the
/// <c>ISubagentSpawner</c> review child (review band, token <c>SubagentUsage</c>), and doc 16 <c>IVmBackend</c> teardown (destroy VM,
/// scrub disk).
/// </remarks>
public interface ISandboxSession
{
    string SessionId { get; }

    /// <summary>Runs one task through the coding harness inside the VM.</summary>
    Task<SandboxTaskResult> RunTaskAsync(string task, CancellationToken ct = default);

    /// <summary>Collects CI result, diff size, review band and token use for the whole branch.</summary>
    Task<BranchMeasurement> CollectAsync(CancellationToken ct = default);

    /// <summary>Destroys the VM. Safe to call more than once.</summary>
    Task TeardownAsync(CancellationToken ct = default);
}

/// <summary>Provisions sandbox VMs. A failed provision must leave nothing behind; the host cleans up its own partial work.</summary>
/// <remarks>Maps to doc 16 environments plus <c>IVmBackend</c> create and start in <c>Bazaar.Server.Vms</c>, with a no-Passport profile.</remarks>
public interface ISandboxVmHost
{
    Task<ISandboxSession> ProvisionAsync(SandboxVmSpec spec, CancellationToken ct = default);
}

/// <summary>
/// Host-side push of a sandbox branch (DECISIONS-LOG C05: pushes are host-side, tokens never enter a VM). It may push only
/// <c>sandbox/*</c> refs and never the default branch.
/// </summary>
/// <remarks>
/// Maps to doc 17 <c>IHostPushPort</c> and <c>DefaultBranchGuard</c> in #788's <c>Bazaar.Server.CodingGit</c>. That port opens an
/// <c>agent/*</c> draft PR; the adapter pushes the ref only and opens no PR. Returns the ref that was pushed.
/// </remarks>
public interface ISandboxPushGate
{
    Task<string> PushAsync(SandboxPushRequest request, CancellationToken ct = default);
}

/// <summary>
/// The quota or Work-Life window refused to start a branch. It is not a <see cref="SandboxRequestRejectedException"/>: a guard
/// violation aborts the whole What If run, this aborts only the branch (PROPOSED, doc 26 gap decisions).
/// </summary>
public sealed class SandboxQuotaRefusedException(SandboxQuotaRefusal refusal)
    : InvalidOperationException($"the sandbox may not start now ({refusal})")
{
    public SandboxQuotaRefusal Refusal { get; } = refusal;
}

/// <summary>The sandbox run could not finish. The message names a stage only; it never carries host or guest error text.</summary>
public sealed class SandboxRunFailedException(string stage) : InvalidOperationException($"Sandbox run failed at stage '{stage}'.")
{
    public string Stage { get; } = stage;
}
