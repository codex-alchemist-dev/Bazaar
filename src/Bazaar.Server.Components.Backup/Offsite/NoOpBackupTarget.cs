using Bazaar.Contracts.Backup;

namespace Bazaar.Server.Components.Backup.Offsite;

/// <summary>
/// The target while <c>backup.offsite_target</c> is <c>none</c>: uploads nothing and lists nothing, so nothing leaves the install.
/// <see cref="Name"/> is <see cref="TargetName"/>, which callers use to report the status "none".
/// </summary>
public sealed class NoOpBackupTarget(TimeProvider? time = null) : IBackupTarget
{
    public const string TargetName = "none";

    public string Name => TargetName;

    /// <summary>Returns an empty snapshot id and sends no bytes.</summary>
    public Task<OffsiteSnapshot> PutDirectoryAsync(string localDirectory, IReadOnlyList<string> tags, CancellationToken ct) =>
        Task.FromResult(new OffsiteSnapshot(string.Empty, (time ?? TimeProvider.System).GetUtcNow(), tags));

    public Task<IReadOnlyList<OffsiteSnapshot>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OffsiteSnapshot>>([]);
}
