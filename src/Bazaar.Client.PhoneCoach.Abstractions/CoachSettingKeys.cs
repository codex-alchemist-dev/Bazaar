namespace Bazaar.Client.PhoneCoach;

/// <summary>Setting keys of the phone coach module (doc 93, Phone gestures and discoverability).</summary>
public static class CoachSettingKeys
{
    /// <summary>
    /// <c>true</c> once the person has dismissed or finished the first-run coach; person scope, kept on the device. The default is
    /// <c>false</c>, so the coach shows once on a fresh install. Replaying it from settings does not clear this flag.
    /// </summary>
    public const string Seen = "client.phone_coach_seen";
}
