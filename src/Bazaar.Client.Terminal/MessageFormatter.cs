using System.Globalization;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Terminal;

/// <summary>Who wrote a message, as the terminal shows it.</summary>
/// <param name="Name">Display name.</param>
/// <param name="IsAgent">True for an agent; its messages carry an <c>[agent]</c> marker.</param>
public sealed record Speaker(string Name, bool IsAgent)
{
    /// <summary>The name with the agent marker when it applies.</summary>
    public string Label => IsAgent ? $"{Name} [agent]" : Name;
}

/// <summary>Pure text formatting for the terminal. No console or socket code, so it can be unit tested.</summary>
public static class MessageFormatter
{
    // Continuation lines line up under the text of the first line: "[HH:mm] " is 8 characters.
    private const string _continuationIndent = "        ";

    /// <summary>Formats one message as <c>[HH:mm] speaker: text</c>, with the time in <paramref name="offset"/>.</summary>
    public static string Format(WireGcMessage message, Speaker speaker, TimeSpan offset)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(speaker);

        string time = message.Ts.ToOffset(offset).ToString("HH:mm", CultureInfo.InvariantCulture);
        string edited = message.Edited ? " (edited)" : string.Empty;
        string text = message.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\n" + _continuationIndent, StringComparison.Ordinal);
        return $"[{time}] {speaker.Label}{edited}: {text}";
    }

    /// <summary>
    /// Works out the speaker. A message this terminal sent is <paramref name="ownName"/>; otherwise a known member's
    /// entry; otherwise the first eight characters of the author id.
    /// </summary>
    public static Speaker Resolve(WireGcMessage message, bool isOwn, string ownName, IReadOnlyDictionary<Guid, Speaker> members)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (isOwn) return new Speaker(ownName, IsAgent: false);
        if (members.TryGetValue(message.AuthorId, out var known)) return known;
        return new Speaker(message.AuthorId.ToString("N", CultureInfo.InvariantCulture)[..8], IsAgent: false);
    }

    /// <summary>A connection or error line, such as <c>-- connected --</c>.</summary>
    public static string Status(string text) => $"-- {text} --";
}
