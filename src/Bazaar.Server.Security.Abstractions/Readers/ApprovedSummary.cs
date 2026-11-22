namespace Bazaar.Server.Security.Readers;

/// <summary>
/// A summary the owner has approved for use in a fresh session. Created only by <see cref="Approve"/>, which needs an
/// <see cref="OwnerPrincipal"/>, so a summary produced by a tainted session cannot reach a fresh seed unreviewed.
/// </summary>
public sealed class ApprovedSummary
{
    private ApprovedSummary(string ownerId, string text)
    {
        OwnerId = ownerId;
        Text = text;
    }

    /// <summary>The owner who approved the summary.</summary>
    public string OwnerId { get; }

    /// <summary>The approved summary text.</summary>
    public string Text { get; }

    /// <summary>Records the owner's approval of a summary.</summary>
    /// <exception cref="ArgumentException">The summary is blank.</exception>
    public static ApprovedSummary Approve(OwnerPrincipal owner, string text)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("An approved summary must not be blank.", nameof(text));
        }

        return new ApprovedSummary(owner.OwnerId, text);
    }
}
