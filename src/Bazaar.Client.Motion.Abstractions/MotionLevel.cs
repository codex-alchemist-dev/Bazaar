namespace Bazaar.Client.Motion;

/// <summary>How much the client moves (doc 93, liquid motion language). Selected by the setting <see cref="MotionSettingKeys.MotionLevel"/>.</summary>
public enum MotionLevel
{
    /// <summary>Phone default: droplets, squash and stretch, springs with one overshoot.</summary>
    Liquid,

    /// <summary>Desktop default: the same ideas at about half the distance and duration, no merge or wobble.</summary>
    Subtle,

    /// <summary>Fades of 90 ms or less, no travel, no squash. Also what the OS reduced-motion setting selects.</summary>
    Reduced,
}

/// <summary>Which family of device the client runs on; decides the default level when the person has not chosen one.</summary>
public enum MotionPlatform
{
    Desktop,
    Phone,
}

/// <summary>Setting keys of the motion module (doc 93, settings table).</summary>
public static class MotionSettingKeys
{
    /// <summary>Enum liquid, subtle or reduced; person scope; liquid on phone, subtle on desktop. The OS reduced-motion setting forces reduced.</summary>
    public const string MotionLevel = "client.motion_level";
}
