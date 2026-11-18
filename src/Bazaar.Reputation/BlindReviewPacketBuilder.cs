using System.Text.RegularExpressions;

namespace Bazaar.Reputation;

/// <summary>
/// Drops the author's identity and scrubs it from the free-text fields by exact match (case-insensitive, any whitespace
/// run matches any whitespace run, never inside a longer word). Not a stylometry defence (doc 11, "Review packet").
/// </summary>
public sealed class BlindReviewPacketBuilder(TimeProvider? clock = null) : IReviewPacketBuilder
{
    /// <summary>Replacement text for a scrubbed identity string.</summary>
    public const string Placeholder = "[redacted]";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public ReviewPacketResult Build(ReviewSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var scrub = BuildScrubber(subject.Author);
        var count = 0;
        var title = Scrub(subject.Title, scrub, ref count);
        var description = Scrub(subject.Description, scrub, ref count);
        var rubric = Scrub(subject.RubricText, scrub, ref count);
        var packet = new ReviewPacket(
            Guid.CreateVersion7(),
            subject.WorkItemRef,
            subject.SubjectRef,
            subject.ContentRef,
            title,
            description,
            rubric,
            _clock.GetUtcNow());
        return new ReviewPacketResult(packet, count);
    }

    private static Regex? BuildScrubber(AuthorIdentity author)
    {
        // Longest first, so "Haiku 5.5" wins over a shorter needle that is part of it.
        var needles = new[] { author.AgentId, author.DisplayName, author.ModelBadge, author.ModelFamily }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .Select(n => string.Join(@"\s+", n.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)))
            .ToArray();
        if (needles.Length == 0)
        {
            return null;
        }

        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", needles)})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static string Scrub(string text, Regex? scrub, ref int count)
    {
        if (scrub is null || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var n = 0;
        var result = scrub.Replace(text, _ =>
        {
            n++;
            return Placeholder;
        });
        count += n;
        return result;
    }
}
