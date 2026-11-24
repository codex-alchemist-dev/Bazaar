namespace Bazaar.Server.Setup.Storage;

/// <summary>What the pre-start volume bootstrap did (doc 63 section 10, setup mechanics).</summary>
public enum StorageBootstrapOutcome
{
    /// <summary>The plan asks for no volume. Nothing was read or written.</summary>
    NotRequested,

    /// <summary>The volume was created, and the host config file was written.</summary>
    Created,

    /// <summary>A volume matching the plan already exists. Nothing was created; the host config file was written if it was missing.</summary>
    Unchanged,

    /// <summary>The volume could not be created or does not match the plan. Nothing was overwritten.</summary>
    Refused,
}

/// <summary>The result of the bootstrap. <paramref name="Reason"/> is a stable code and never holds a key or a secret.</summary>
/// <param name="ExitCode">0 unless <see cref="StorageBootstrapOutcome.Refused"/>; then the code the process exits with.</param>
/// <param name="Reason">A stable reason code for a refusal, for example <c>data_volume_already_initialised</c>; null otherwise.</param>
/// <param name="Detail">One line for the operator. Paths and ids only.</param>
public sealed record StorageBootstrapResult(StorageBootstrapOutcome Outcome, int ExitCode, string? Reason, string? Detail)
{
    /// <summary>True when the host may go on to start.</summary>
    public bool MayStart => Outcome != StorageBootstrapOutcome.Refused;
}

/// <summary>
/// Creates the encrypted data volume a setup plan asks for, before the host is built. The setup apply itself runs with the
/// database already up, so it can only verify (<c>storage.volume</c> step).
/// </summary>
public interface IStorageVolumeBootstrap
{
    /// <summary>Idempotent: a volume that already matches the plan is left alone, and one that differs is refused, never replaced.</summary>
    Task<StorageBootstrapResult> EnsureAsync(SetupPlan plan, CancellationToken cancellationToken);
}
