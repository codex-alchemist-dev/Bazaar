using System.Text;

namespace Bazaar.Server.Vault.Markdown;

/// <summary>
/// A parsed Vault document. <see cref="Source"/> is the exact text it was parsed from; <see cref="Render"/> applies
/// byte-level edits to it, so untouched bytes are preserved exactly.
/// </summary>
public sealed record VaultDocument(string Source, VaultFrontMatter? FrontMatter, IReadOnlyList<WikiLink> Links)
{
    /// <summary>Applies non-overlapping edits. With no edits the result equals <see cref="Source"/>.</summary>
    public string Render(IReadOnlyList<SourceEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var ordered = edits.OrderBy(e => e.Start).ToList();
        var builder = new StringBuilder(Source.Length);
        var cursor = 0;
        foreach (var edit in ordered)
        {
            if (edit.Start < cursor || edit.Length < 0 || edit.Start + edit.Length > Source.Length)
            {
                throw new ArgumentException($"Edit at {edit.Start} (length {edit.Length}) overlaps another edit or is out of range.", nameof(edits));
            }

            builder.Append(Source, cursor, edit.Start - cursor).Append(edit.Replacement);
            cursor = edit.Start + edit.Length;
        }

        builder.Append(Source, cursor, Source.Length - cursor);
        return builder.ToString();
    }
}
