using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// Operator limits for reactions and the typing signal (docs/design/01 "Reactions" settings, docs/design/02 "Typing signal"
/// settings). Defaults are the doc defaults. They bind from <c>Bazaar:GcReactions</c>; the settings registry takes over the
/// same keys once the module can read it (the store lives in Bazaar.Server, which a module may not reference).
/// </summary>
public sealed class GcReactionsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bazaar:GcReactions";

    /// <summary><c>gc.reactions_enabled</c>.</summary>
    public bool ReactionsEnabled { get; set; } = true;

    /// <summary><c>gc.reaction_emoji_max_bytes</c>: the longest emoji, in UTF-8 bytes.</summary>
    public int EmojiMaxBytes { get; set; } = 32;

    /// <summary><c>gc.reaction_max_distinct_per_message</c>.</summary>
    public int MaxDistinctPerMessage { get; set; } = 20;

    /// <summary><c>gc.reaction_max_per_person_per_message</c>.</summary>
    public int MaxPerPersonPerMessage { get; set; } = 10;

    /// <summary><c>gc.reaction_rate_per_minute</c>: reaction requests one person may make in one GC per minute.</summary>
    public int RatePerMinute { get; set; } = 30;

    /// <summary><c>wire.typing_enabled</c>.</summary>
    public bool TypingEnabled { get; set; } = true;

    /// <summary><c>wire.typing_ttl_s</c>: seconds a typing signal lives without a refresh.</summary>
    public int TypingTtlS { get; set; } = 5;

    /// <summary><c>wire.typing_min_interval_s</c>: the least time between two accepted signals from one person in one GC.</summary>
    public int TypingMinIntervalS { get; set; } = 2;
}

/// <summary>Reads and checks <see cref="GcReactionsOptions"/>. A value outside its range fails at start, so a typo never turns a limit off.</summary>
public static class GcReactionsOptionsReader
{
    /// <summary>Reads the section; absent keys keep the defaults.</summary>
    /// <exception cref="InvalidOperationException">A value does not parse or is out of range.</exception>
    public static GcReactionsOptions Read(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var o = new GcReactionsOptions
        {
            ReactionsEnabled = Bool(section, "ReactionsEnabled", true),
            EmojiMaxBytes = Int(section, "EmojiMaxBytes", 32),
            MaxDistinctPerMessage = Int(section, "MaxDistinctPerMessage", 20),
            MaxPerPersonPerMessage = Int(section, "MaxPerPersonPerMessage", 10),
            RatePerMinute = Int(section, "RatePerMinute", 30),
            TypingEnabled = Bool(section, "TypingEnabled", true),
            TypingTtlS = Int(section, "TypingTtlS", 5),
            TypingMinIntervalS = Int(section, "TypingMinIntervalS", 2),
        };
        var failures = Validate(o);
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(' ', failures));
        return o;
    }

    /// <summary>Why <paramref name="options"/> is not acceptable; empty when it is.</summary>
    public static IReadOnlyList<string> Validate(GcReactionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        Range(failures, "EmojiMaxBytes", options.EmojiMaxBytes, 4, 128);
        Range(failures, "MaxDistinctPerMessage", options.MaxDistinctPerMessage, 1, 100);
        Range(failures, "MaxPerPersonPerMessage", options.MaxPerPersonPerMessage, 1, 50);
        Range(failures, "RatePerMinute", options.RatePerMinute, 1, 600);
        Range(failures, "TypingTtlS", options.TypingTtlS, 2, 15);
        Range(failures, "TypingMinIntervalS", options.TypingMinIntervalS, 0, options.TypingTtlS);
        return failures;
    }

    private static int Int(IConfigurationSection section, string key, int fallback)
    {
        if (section[key] is not { Length: > 0 } text)
            return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"{GcReactionsOptions.SectionName}:{key} must be a whole number, got '{text}'.");
        return value;
    }

    private static bool Bool(IConfigurationSection section, string key, bool fallback)
    {
        if (section[key] is not { Length: > 0 } text)
            return fallback;
        if (!bool.TryParse(text, out var value))
            throw new InvalidOperationException($"{GcReactionsOptions.SectionName}:{key} must be true or false, got '{text}'.");
        return value;
    }

    private static void Range(List<string> failures, string name, int value, int min, int max)
    {
        if (value < min || value > max)
            failures.Add($"{GcReactionsOptions.SectionName}:{name} must be from {min} to {max}, got {value}.");
    }
}
