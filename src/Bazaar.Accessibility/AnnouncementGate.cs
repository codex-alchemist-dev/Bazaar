namespace Bazaar.Accessibility;

/// <summary>What a live region announces (doc 49 section 2).</summary>
public enum AnnouncementCategory
{
    /// <summary>A new message in the open group chat.</summary>
    GroupMessage,

    /// <summary>A mention of the viewer.</summary>
    Mention,

    /// <summary>An approval request that waits for the viewer.</summary>
    ApprovalRequest,

    /// <summary>A Kodo incident.</summary>
    KodoIncident,
}

/// <summary>Per-category settings: whether it speaks at all, and the shortest gap between two announcements.</summary>
public sealed record AnnouncementCategorySettings(bool Enabled, TimeSpan MinimumInterval)
{
    /// <summary>The shipped default for a category. Chatty categories wait longer than ones that need attention.</summary>
    public static AnnouncementCategorySettings Default(AnnouncementCategory category) => category switch
    {
        AnnouncementCategory.GroupMessage => new(true, TimeSpan.FromSeconds(4)),
        AnnouncementCategory.Mention => new(true, TimeSpan.FromSeconds(2)),
        AnnouncementCategory.ApprovalRequest => new(true, TimeSpan.FromSeconds(1)),
        AnnouncementCategory.KodoIncident => new(true, TimeSpan.FromSeconds(1)),
        _ => new(true, TimeSpan.FromSeconds(4)),
    };
}

/// <summary>The answer for one announcement.</summary>
/// <param name="Speak">True when the live region should read <see cref="Text"/> now.</param>
/// <param name="Text">The announcement text, unchanged from the caller.</param>
/// <param name="SuppressedBefore">
/// How many announcements of this category were held back since the last spoken one. The head phrases it, for example
/// as an ICU plural "and {n, plural, one {# more} other {# more}}", so the count is never built by concatenation.
/// </param>
public sealed record AnnouncementDecision(bool Speak, string Text, int SuppressedBefore);

/// <summary>
/// Decides whether a live region speaks an announcement (doc 49 section 2: announcements are rate-limited and can be
/// turned off per category). A category that is off never speaks. Otherwise the first announcement speaks at once, and
/// later ones within the category's minimum interval are held back and counted, so a busy chat does not talk over the
/// screen reader. Categories are independent: a mention is never held back by chat traffic. Thread-safe. Pure apart
/// from the clock, which is injected so tests control time.
/// </summary>
public sealed class AnnouncementGate
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<AnnouncementCategory, AnnouncementCategorySettings> _settings = [];
    private readonly Dictionary<AnnouncementCategory, long> _lastSpoken = [];
    private readonly Dictionary<AnnouncementCategory, int> _suppressed = [];

    public AnnouncementGate(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Replaces one category's settings. A negative interval is not allowed.</summary>
    public void Configure(AnnouncementCategory category, AnnouncementCategorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MinimumInterval, TimeSpan.Zero);

        lock (_gate)
        {
            _settings[category] = settings;
        }
    }

    /// <summary>Asks whether <paramref name="text"/> should be spoken now in <paramref name="category"/>.</summary>
    public AnnouncementDecision Decide(AnnouncementCategory category, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        lock (_gate)
        {
            AnnouncementCategorySettings settings = _settings.TryGetValue(category, out var s) ? s : AnnouncementCategorySettings.Default(category);
            if (!settings.Enabled)
            {
                return new AnnouncementDecision(false, text, 0);
            }

            long now = _time.GetTimestamp();
            bool due = !_lastSpoken.TryGetValue(category, out long last)
                || _time.GetElapsedTime(last, now) >= settings.MinimumInterval;
            int held = _suppressed.GetValueOrDefault(category);
            if (!due)
            {
                _suppressed[category] = held + 1;
                return new AnnouncementDecision(false, text, 0);
            }

            _lastSpoken[category] = now;
            _suppressed[category] = 0;
            return new AnnouncementDecision(true, text, held);
        }
    }
}
