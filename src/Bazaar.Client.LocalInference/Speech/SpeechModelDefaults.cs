namespace Bazaar.Client.LocalInference.Speech;

/// <summary>Default speech model ids from the doc 31 settings table. Operators change them in settings; these are safe shipped defaults.</summary>
public static class SpeechModelDefaults
{
    /// <summary>Default for <c>coworker.model.stt.windows</c>.</summary>
    public const string WindowsStt = "whisper";

    /// <summary>Default for <c>coworker.model.stt.android</c>.</summary>
    public const string AndroidStt = "whisper-small";
}
