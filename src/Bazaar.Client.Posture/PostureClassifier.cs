namespace Bazaar.Client.Posture;

/// <summary>
/// What a platform reports about the fold: whether a hinge exists, its orientation, the platform's own flat or half-open state
/// (Android <c>FoldingFeature.State</c>) and, where available, the angle in degrees (0 closed, 180 flat).
/// </summary>
public readonly record struct FoldReading(bool HasHinge, HingeOrientation Orientation, bool? ReportedFlat, double? AngleDegrees);

/// <summary>Pure posture classifier. Android WindowManager specifics stay in the head that builds <see cref="FoldReading"/>.</summary>
public static class PostureClassifier
{
    /// <summary>
    /// Classifies a reading. With an angle, the thresholds decide (with hysteresis against <paramref name="previous"/>); without one,
    /// the platform's reported state decides; with neither, a hinge counts as half open and no hinge as flat.
    /// </summary>
    public static DevicePosture Classify(in FoldReading reading, DevicePosture? previous = null, PostureThresholds? thresholds = null)
    {
        if (!reading.HasHinge)
        {
            return DevicePosture.Flat;
        }

        var t = (thresholds ?? PostureThresholds.Default).Clamped();
        bool flat;
        if (reading.AngleDegrees is { } angle)
        {
            var wasFlat = previous == DevicePosture.Flat;
            flat = wasFlat ? angle >= t.FlatExitDegrees : angle >= t.FlatEnterDegrees;
        }
        else
        {
            flat = reading.ReportedFlat ?? false;
        }

        if (flat)
        {
            return DevicePosture.Flat;
        }

        return reading.Orientation switch
        {
            HingeOrientation.Vertical => DevicePosture.Book,
            HingeOrientation.Horizontal => DevicePosture.Tabletop,
            _ => DevicePosture.HalfOpen,
        };
    }

    /// <summary>Builds a full <see cref="PostureState"/> from a reading and the hinge bounds the platform gave.</summary>
    public static PostureState ToState(in FoldReading reading, HingeInset hinge, DevicePosture? previous = null, PostureThresholds? thresholds = null) =>
        new(Classify(reading, previous, thresholds), reading.AngleDegrees, reading.HasHinge ? hinge : HingeInset.None);
}
