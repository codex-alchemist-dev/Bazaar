namespace Bazaar.Server.Twin;

/// <summary>
/// Keeps only the sentences whose <c>source_ref</c> is one of the supplied references (doc 25: "A sentence without a valid
/// source_ref is dropped by the validator"). Pure: no I/O, no clock. A bad sentence is dropped, never repaired.
/// </summary>
public static class TwinRefValidator
{
    public static ValidatedTwinPage Validate(TwinProseRequest request, IEnumerable<TwinProseSentence> draft)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(draft);
        var allowed = new HashSet<string>(TwinRefs.Allowed(request.Facts), StringComparer.Ordinal);

        var accepted = new List<TwinProseSentence>();
        var dropped = new Dictionary<TwinDropReason, int>();
        foreach (var sentence in draft)
        {
            var reason = Classify(sentence, allowed);
            if (reason is null)
            {
                accepted.Add(sentence with { Text = sentence.Text.Trim() });
            }
            else
            {
                dropped[reason.Value] = dropped.GetValueOrDefault(reason.Value) + 1;
            }
        }

        return new ValidatedTwinPage(request.Project, request.Page, request.Title, accepted, dropped);
    }

    private static TwinDropReason? Classify(TwinProseSentence sentence, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(sentence.SourceRef))
        {
            return TwinDropReason.MissingRef;
        }

        if (!allowed.Contains(sentence.SourceRef))
        {
            return TwinDropReason.UnknownRef;
        }

        if (string.IsNullOrWhiteSpace(sentence.Text))
        {
            return TwinDropReason.EmptyText;
        }

        // The cited reference comes from a fact subject, so it is checked as well: it ends up inside an HTML comment.
        if (IsUnsafe(sentence.Text) || IsUnsafe(sentence.SourceRef))
        {
            return TwinDropReason.UnsafeText;
        }

        return null;
    }

    private static bool IsUnsafe(string text) =>
        text.IndexOfAny(['\r', '\n']) >= 0
        || text.Contains("-->", StringComparison.Ordinal)
        || text.Contains("<!--", StringComparison.Ordinal);
}
