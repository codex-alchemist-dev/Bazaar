namespace Bazaar.Server.WhatIf;

/// <summary>The sandbox GC a fork produced, linked to its source.</summary>
public sealed record WhatIfFork(Guid SandboxGcId, Guid SourceGcId, long SourceSeq, string? VaultCommit);

/// <summary>
/// Forks a GC at a decision seq into a <c>sandbox</c> GC. The <c>fork_link</c> row belongs to the Time Machine module (doc 26 section 4,
/// Bazaar.Server.TimeMachine.Abstractions, built in parallel). FUTURE BINDING: once that module lands, an adapter in the host maps this seam to
/// its fork operation; What If never writes <c>fork_link</c> itself and copies no messages.
/// </summary>
public interface IWhatIfForkSource
{
    Task<WhatIfFork> ForkAsync(Guid sourceGcId, long sourceSeq, CancellationToken ct = default);
}

/// <summary>Posts the single <c>system</c> entry a finished run leaves in the main GC (doc 26 section 3).</summary>
public interface IWhatIfReportSink
{
    Task PostSystemEntryAsync(Guid gcId, string text, CancellationToken ct = default);
}
