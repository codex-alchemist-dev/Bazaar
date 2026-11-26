namespace Bazaar.Server.DataVolume;

/// <summary>
/// The OS-specific reader of a data volume's state (doc 63 section 3; <c>bitlocker-vhdx</c> on Windows, <c>luks2</c> on
/// Linux). It runs in the unprivileged host and only reports; opening and closing a volume belongs to the privileged
/// volume unit.
/// </summary>
public interface IDataVolumeProvider
{
    /// <summary>The provider kind stored in the descriptor, for example <c>luks2</c>.</summary>
    string Kind { get; }

    /// <summary>Reports the state of the volume mounted at <paramref name="dataRoot"/>, with links and mount points resolved.</summary>
    ValueTask<DataVolumeStatus> InspectAsync(string dataRoot, CancellationToken cancellationToken);
}

/// <summary>The start guard (doc 63 section 5). The host calls it before PostgreSQL starts and Kodo calls it as a probe.</summary>
public interface IDataVolumeGuard
{
    /// <summary>Verifies the data volume. Throws <see cref="DataVolumeRefusedException"/> on any failed check; never repairs or creates.</summary>
    ValueTask<DataVolumeVerification> VerifyAsync(CancellationToken cancellationToken);

    /// <summary>Checks that each path resolves inside the data root with no link on the way. Throws <see cref="DataVolumeRefusedException"/> otherwise.</summary>
    void EnsureInsideDataRoot(IEnumerable<string> paths);
}
