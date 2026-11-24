using Bazaar.Server.DataVolume;

namespace Bazaar.Server.Setup.Storage;

/// <summary>
/// The apply step for the plan's <c>storage</c> section (doc 63 section 10, setup mechanics). It creates nothing: the volume
/// is created before the host is built, because the apply runs with the database already up. The step runs the start guard
/// and fails the apply when the mounted volume is not the one the plan names. Running it again changes nothing.
/// </summary>
public sealed class StorageVolumeStep(IEnumerable<IDataVolumeGuard> guards) : ISetupStep
{
    /// <summary>Stable id, written to the audit row.</summary>
    public const string StepId = "storage.volume";

    /// <summary>Before every step that touches data.</summary>
    public const int StepOrder = 20;

    private readonly IDataVolumeGuard? _guard = guards.FirstOrDefault();

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers => ["storage"];

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var volume = plan.Storage.Volume;
        if (string.IsNullOrWhiteSpace(volume.Provider)) return SetupStepResult.Ok;

        if (_guard is null)
            return new SetupStepResult(false, "The data volume guard is not registered in this host, so the volume the plan names cannot be verified.");

        DataVolumeVerification verified;
        try
        {
            verified = await _guard.VerifyAsync(cancellationToken);
        }
        catch (DataVolumeRefusedException refusal)
        {
            return new SetupStepResult(false, $"The data volume was refused ({refusal.Code}). Start the host with this plan so the volume is created before the database starts.");
        }

        if (!string.Equals(verified.Descriptor.ProviderKind, volume.Provider, StringComparison.Ordinal))
            return new SetupStepResult(false, "The mounted data volume uses another provider than storage.volume.provider names. A volume is never re-created by setup.");

        if (!string.Equals(verified.Descriptor.InstanceClass.ToString(), plan.Storage.InstanceClass, StringComparison.OrdinalIgnoreCase))
            return new SetupStepResult(false, "The mounted data volume has another instance class than storage.instanceClass names. An instance class never changes.");

        return SetupStepResult.Ok;
    }
}
