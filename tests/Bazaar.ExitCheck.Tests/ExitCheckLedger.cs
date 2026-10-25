namespace Bazaar.ExitCheck.Tests;

/// <summary>One work package the v0.3 exit check waits for (docs/design/34-testing-strategy.md; plan Batch 3).</summary>
/// <param name="Id">Plan id, such as <c>W3</c>.</param>
/// <param name="What">What the exit check needs from it.</param>
/// <param name="Merged">True once it is on the branch this project builds from.</param>
public sealed record Prerequisite(string Id, string What, bool Merged);

/// <summary>
/// The tracking mechanism for "expected to fail until ready". Doc 34 forbids <c>Skip</c>, quarantine and
/// retry-to-green, so the exit check is never skipped and never reports a fake pass: while a prerequisite is
/// unmerged, <see cref="ExitCheckServers.StartAsync"/> throws <see cref="PendingPrerequisitesException"/> naming
/// what is missing. This project is outside <c>Bazaar.slnx</c> and runs in the non-blocking CI job
/// "v0.3 exit check (tracking)" until every <see cref="Merged"/> flag is true; README.md has the graduation steps.
/// Flip a flag in the same PR that merges its work package.
/// </summary>
public static class ExitCheckLedger
{
    /// <summary>Server-side pieces. The client (W7), policy (GW7) and model seam (GW10) are merged and used for real.</summary>
    public static IReadOnlyList<Prerequisite> ServerSide { get; } =
    [
        new("G1", "migration 0001: gc, gc_event, gc_message, gc_message_version, per-GC counters", false),
        new("G2", "GC append: gap-free n and ev inside the write transaction", false),
        new("G3", "GC read paths: pages after (n, ev), history", false),
        new("HOST1", "composed host that serves the socket on a test port", false),
        new("W3", "connection manager: upgrade, hello/welcome, authenticator hook", false),
        new("W4", "subscription engine: replay paging, splice, resync", false),
        new("W5", "dispatcher: LISTEN to sockets, bounded queue, lagged", false),
        new("W6", "idempotent request layer for gc.send", false),
        new("PRH", "protocol harness: in-process Kestrel with a fake authenticator", false),
    ];

    /// <summary>The prerequisites not yet merged.</summary>
    public static IReadOnlyList<Prerequisite> Pending => [.. ServerSide.Where(p => !p.Merged)];
}

/// <summary>Thrown instead of skipping: the exit check cannot run yet. Fails the test with the missing packages listed.</summary>
public sealed class PendingPrerequisitesException(IReadOnlyList<Prerequisite> pending)
    : Exception("v0.3 exit check is waiting for: " + string.Join("; ", pending.Select(p => $"{p.Id} ({p.What})")));
