namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>How a canary was used.</summary>
public enum CanaryUseKind
{
    /// <summary>The provider saw a read of the canary file on the share.</summary>
    FileRead,

    /// <summary>The canary value turned up outside the guest, for example at the egress proxy.</summary>
    TokenSeen,
}

/// <summary>
/// One use of a planted canary. <see cref="CanaryId"/> is the SHA-256 fingerprint of the canary value in lower-case hex; the value
/// itself is never reported. <see cref="Source"/> names the observer (a provider name or a caller-supplied source).
/// </summary>
public sealed record CanaryUseReport(
    Guid AgentId,
    Guid VmId,
    Guid MountId,
    string CanaryId,
    string RelativePath,
    CanaryUseKind Kind,
    string Source,
    DateTimeOffset At);

/// <summary>
/// Receives canary use reports. The agent-safety module implements it and turns a report into the page-and-freeze action
/// (doc 15 "Credentials, mounts, browser", PROPOSED there). A report must not be dropped: a sink that throws makes the manager
/// surface the failure to the observer.
/// </summary>
public interface ICanarySink
{
    ValueTask ReportCanaryUseAsync(CanaryUseReport report, CancellationToken ct);
}
