namespace Bazaar.Kodo.Settings;

/// <summary>The shape of a setting value. Values are stored as canonical text and parsed strictly.</summary>
public enum KodoSettingType
{
    /// <summary>A whole number: a count, a window in minutes or seconds, or a timeout.</summary>
    WholeNumber,

    /// <summary>A decimal number, such as an SLO target in percent.</summary>
    Number,

    /// <summary>A comma-separated list of whole numbers, such as the restart backoff ladder.</summary>
    IntegerList,
}

/// <summary>Which side of the floor is the loosening side.</summary>
public enum KodoFloorRule
{
    /// <summary>A larger value is the safer choice; a value below the floor loosens the setting.</summary>
    AtLeast,

    /// <summary>A smaller value is the safer choice; a value above the floor loosens the setting.</summary>
    AtMost,
}

/// <summary>
/// One entry in the Kodo settings catalogue (doc 46 section 20). The default and the floor are both
/// canonical text, so the catalogue can be checked by round-trip.
/// </summary>
/// <param name="Key">The dotted setting key, under the <c>kodo.</c> prefix.</param>
/// <param name="Type">How the value is parsed.</param>
/// <param name="Default">The safe shipped default, in canonical text.</param>
/// <param name="Min">The smallest accepted number (applied to every list element).</param>
/// <param name="Max">The largest accepted number (applied to every list element).</param>
/// <param name="Floor">The loosest value an operator may set.</param>
/// <param name="FloorRule">Which direction loosens the setting.</param>
/// <param name="Description">One sentence on what the setting controls.</param>
public sealed record KodoSettingDefinition(
    string Key,
    KodoSettingType Type,
    string Default,
    decimal Min,
    decimal Max,
    decimal Floor,
    KodoFloorRule FloorRule,
    string Description);
