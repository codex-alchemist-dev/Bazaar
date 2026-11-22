namespace Bazaar.Rubedo.Rules;

/// <summary>
/// The typed values of the nine identifiers in <c>RubedoSchema.Standard</c> (doc 41a section 3). Immutable and pure: the head
/// reads its own device state and builds one of these, so Rubedo itself never touches a window or a sensor.
/// </summary>
public sealed record RubedoContext
{
    private RubedoContext()
    {
    }

    public string DeviceClass { get; private init; } = string.Empty;

    public string Display { get; private init; } = string.Empty;

    public string Posture { get; private init; } = string.Empty;

    public decimal WidthDp { get; private init; }

    public decimal HeightDp { get; private init; }

    public decimal TextScale { get; private init; }

    public bool ReduceMotion { get; private init; }

    public bool IsOwner { get; private init; }

    public string ColorScheme { get; private init; } = string.Empty;

    /// <summary>Builds a context from the values a head reads from its device.</summary>
    public static RubedoContext ForDevice(
        string deviceClass,
        string display,
        string posture,
        decimal widthDp,
        decimal heightDp,
        decimal textScale,
        bool reduceMotion,
        bool isOwner,
        string colorScheme)
    {
        ArgumentNullException.ThrowIfNull(deviceClass);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(posture);
        ArgumentNullException.ThrowIfNull(colorScheme);

        return new RubedoContext
        {
            DeviceClass = deviceClass,
            Display = display,
            Posture = posture,
            WidthDp = widthDp,
            HeightDp = heightDp,
            TextScale = textScale,
            ReduceMotion = reduceMotion,
            IsOwner = isOwner,
            ColorScheme = colorScheme,
        };
    }

    /// <summary>The dictionary <see cref="RubedoEngine.Evaluate"/> takes, with each value typed as the schema declares.</summary>
    public IReadOnlyDictionary<string, object> ToValues() => new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["deviceClass"] = DeviceClass,
        ["display"] = Display,
        ["posture"] = Posture,
        ["widthDp"] = WidthDp,
        ["heightDp"] = HeightDp,
        ["textScale"] = TextScale,
        ["reduceMotion"] = ReduceMotion,
        ["isOwner"] = IsOwner,
        ["colorScheme"] = ColorScheme,
    };
}
