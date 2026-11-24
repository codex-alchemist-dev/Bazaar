using Android.Content;
using Bazaar.Client.Motion;

namespace Bazaar.Client.Android;

/// <summary>
/// The Android reduced-motion flag (doc 93): the "Remove animations" accessibility switch and the developer option both set
/// <c>Settings.Global.animator_duration_scale</c> to 0. The head refreshes the source when the activity resumes, which is when a
/// person returns from changing it. Not built in the Linux cloud session (no Android workload); it is verified by
/// scripts/build-android.sh on a machine that has it. The scale is read through <see cref="Map"/>, which is pure.
/// </summary>
public static class AndroidReducedMotion
{
    /// <summary>Reduced when the animator duration scale is 0 (animations off). A missing setting reads as the default scale of 1.</summary>
    public static bool Map(float animatorDurationScale) => animatorDurationScale == 0f;

    /// <summary>A source over the live system setting.</summary>
    public static PolledOsReducedMotionSource Create(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PolledOsReducedMotionSource(() =>
        {
            // Fully qualified: inside Bazaar.Client the name Settings means the Bazaar.Client.Settings namespace.
            float scale = global::Android.Provider.Settings.Global.GetFloat(context.ContentResolver, global::Android.Provider.Settings.Global.AnimatorDurationScale, 1f);
            return Map(scale);
        });
    }
}
