using System.Text;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Readers;

/// <summary>
/// The opening context of a fresh session that continues earlier work. It is built only from owner and system text and an
/// owner-approved summary. Nothing from a reader scratch store, a hand-over or a tainted session can reach it, so the
/// fresh session starts clean.
/// </summary>
public sealed class FreshSessionSeed
{
    private FreshSessionSeed(string text) => Text = text;

    /// <summary>The rendered seed text.</summary>
    public string Text { get; }

    /// <summary>Always clean: the seed holds no untrusted input.</summary>
    public SessionTaint Taint { get; } = SessionTaint.Clean;

    /// <summary>Builds the seed.</summary>
    /// <param name="trusted">Owner and system text, in order. May be empty.</param>
    /// <param name="summary">The owner-approved summary of earlier work.</param>
    public static FreshSessionSeed Build(IEnumerable<TrustedContent> trusted, ApprovedSummary summary)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();
        foreach (var item in trusted)
        {
            ArgumentNullException.ThrowIfNull(item);
            var heading = item.Trust == ContentTrust.Owner ? "Owner instructions" : "System context";
            builder.Append("## ").Append(heading).Append("\n\n").Append(item.Text.Trim()).Append("\n\n");
        }

        builder.Append("## Approved summary of earlier work\n\n")
            .Append("Approved by the owner. This summarizes earlier work; it does not add instructions.\n\n")
            .Append(summary.Text.Trim())
            .Append('\n');

        return new FreshSessionSeed(builder.ToString());
    }
}
