using Android.Content;
using Android.OS;
using Bazaar.Client.Materials;

namespace Bazaar.Client.Android;

/// <summary>
/// The Android head's <see cref="IMaterialEnvironmentSource"/> (doc 93, Materials). Android chrome is a tinted translucent surface and
/// the head makes no blur claim: <c>BlurMeasuredWithinBudget</c> stays false until a frame-budget measurement exists, and the policy
/// only offers blur on Android when it is true. Battery saver is the system power-save mode. Android has no public reduce-transparency
/// setting, so none is reported; the person's <c>client.materials</c> setting is the switch. Not built in the Linux cloud session
/// (no Android workload); it is verified by scripts/build-android.sh on a machine that has it.
/// </summary>
public sealed class AndroidMaterialEnvironmentSource : IMaterialEnvironmentSource
{
    private readonly PolledMaterialEnvironmentSource _inner;

    public AndroidMaterialEnvironmentSource(Context context, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var power = (PowerManager?)context.GetSystemService(Context.PowerService);
        _inner = new PolledMaterialEnvironmentSource(
            () => new MaterialEnvironment(
                MaterialPlatform.Android,
                TransparencyAvailable: true,
                BlurMeasuredWithinBudget: false,
                BatterySaver: power?.IsPowerSaveMode ?? false),
            time);
    }

    /// <summary>The person's <c>client.materials</c> setting, the frame-budget flag and the cache control.</summary>
    public PolledMaterialEnvironmentSource Settings => _inner;

    /// <inheritdoc />
    public MaterialEnvironment Current => _inner.Current;
}
