namespace Bazaar.Client.Motion;

/// <summary>
/// One level of a named interaction. <see cref="Duration"/> is the time to rest (for a spring, the settle time),
/// <see cref="Amount"/> is the interaction's own strength (see <see cref="InteractionNames"/> for what each preset reads it as)
/// and <see cref="DampingRatio"/> shapes a spring (below 1 overshoots; ignored by the subtle and reduced curves).
/// </summary>
public sealed record InteractionVariant(TimeSpan Duration, double Amount = 0, double DampingRatio = 1);

/// <summary>
/// A catalogue row as a named preset (doc 93: <c>motion.interaction.&lt;name&gt;</c> with <c>liquid</c>, <c>subtle</c> and
/// <c>reduced</c> variants) that a Rubedo document can replace or turn off.
/// </summary>
public sealed record InteractionPreset(string Name, InteractionVariant Liquid, InteractionVariant Subtle, InteractionVariant Reduced, bool Enabled = true)
{
    public InteractionVariant For(MotionLevel level) => level switch
    {
        MotionLevel.Liquid => Liquid,
        MotionLevel.Subtle => Subtle,
        _ => Reduced,
    };
}

/// <summary>The preset names of the micro-interaction catalogue. The comment says what <see cref="InteractionVariant.Amount"/> means for each.</summary>
public static class InteractionNames
{
    /// <summary>Touch ripple on a pressable. Amount: peak opacity of the ring (liquid) or of the tint (subtle, reduced).</summary>
    public const string Ripple = "motion.interaction.ripple";

    /// <summary>Switch toggle. Amount: gain of the stretch while the knob travels (liquid only).</summary>
    public const string Toggle = "motion.interaction.toggle";

    /// <summary>Button press. Amount: pressed scale (liquid) or darken tint (subtle, reduced). Duration is the release.</summary>
    public const string Press = "motion.interaction.press";

    /// <summary>Send button filling when the composer has text. Amount unused.</summary>
    public const string SendFill = "motion.interaction.send-fill";

    /// <summary>Send button launch. Amount: how far the drop rises, in device-independent pixels (liquid) or the tint peak.</summary>
    public const string SendLaunch = "motion.interaction.send-launch";

    /// <summary>Reaction chip added. Amount: scale the chip starts from (liquid).</summary>
    public const string ReactionPop = "motion.interaction.reaction-pop";

    /// <summary>Unread badge appearing or its count changing. Amount: initial speed of the count bump (liquid), in scale units per second.</summary>
    public const string BadgeBump = "motion.interaction.badge-bump";

    public static IReadOnlyList<string> All { get; } = [Ripple, Toggle, Press, SendFill, SendLaunch, ReactionPop, BadgeBump];
}

/// <summary>
/// The registry of interaction presets. Immutable: <see cref="With"/> and <see cref="Without"/> return a new registry, so a theme
/// or motion document can replace or turn off a row without touching the shipped defaults. A name that is missing or disabled
/// resolves to nothing and its plan jumps straight to rest. Safety floors apply on resolve: reduced durations never exceed 90 ms.
/// </summary>
public sealed class InteractionPresets
{
    private readonly Dictionary<string, InteractionPreset> _presets;

    public InteractionPresets(IEnumerable<InteractionPreset> presets) =>
        _presets = presets.ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>The shipped defaults (doc 93 catalogue values, to be tuned on devices).</summary>
    public static InteractionPresets Default { get; } = new(
    [
        new(InteractionNames.Ripple, V(900, 0.35), V(200, 0.12), V(90, 0.2)),
        new(InteractionNames.Toggle, V(360, 1.0, 0.5), V(140), V(0)),
        new(InteractionNames.Press, V(280, 0.96, 0.5), V(140, 0.12), V(90, 0.12)),
        new(InteractionNames.SendFill, V(300, 0, 0.6), V(160), V(90)),
        new(InteractionNames.SendLaunch, V(420, 24), V(160, 0.2), V(90, 0.2)),
        new(InteractionNames.ReactionPop, V(320, 0.3, 0.55), V(160), V(90)),
        new(InteractionNames.BadgeBump, V(360, 6, 0.45), V(160), V(90)),
    ]);

    public IEnumerable<InteractionPreset> All => _presets.Values;

    public bool TryGet(string name, out InteractionPreset preset) => _presets.TryGetValue(name, out preset!);

    /// <summary>The variant for a level with the safety floors applied, or null when the preset is missing or disabled.</summary>
    public InteractionVariant? Resolve(string name, MotionLevel level)
    {
        if (!_presets.TryGetValue(name, out var preset) || !preset.Enabled)
        {
            return null;
        }

        var v = preset.For(level);
        var duration = v.Duration < TimeSpan.Zero ? TimeSpan.Zero : v.Duration;
        if (level == MotionLevel.Reduced && duration > MotionTokens.ReducedFadeCeiling)
        {
            duration = MotionTokens.ReducedFadeCeiling;
        }

        return v with { Duration = duration, DampingRatio = Math.Clamp(v.DampingRatio, 0.05, 2.0) };
    }

    /// <summary>A registry with <paramref name="preset"/> added or replacing the one of the same name.</summary>
    public InteractionPresets With(InteractionPreset preset) =>
        new(_presets.Values.Where(p => p.Name != preset.Name).Append(preset));

    /// <summary>A registry without <paramref name="name"/>: that interaction does nothing.</summary>
    public InteractionPresets Without(string name) => new(_presets.Values.Where(p => p.Name != name));

    private static InteractionVariant V(double ms, double amount = 0, double damping = 1) =>
        new(TimeSpan.FromMilliseconds(ms), amount, damping);
}
