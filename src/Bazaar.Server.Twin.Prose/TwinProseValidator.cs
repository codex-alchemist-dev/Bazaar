namespace Bazaar.Server.Twin.Prose;

/// <summary>A sentence that passed, with the facts it cites.</summary>
public sealed record ValidatedSentence(string Text, string SourceRef, IReadOnlyList<TwinFactRecord> Facts);

/// <summary>Validator output. <see cref="Trust"/> is the highest trust of the facts the kept sentences cite.</summary>
public sealed record ValidatedProse(IReadOnlyList<ValidatedSentence> Sentences, int Dropped, TwinTrust? Trust);

/// <summary>
/// Keeps only draft sentences with a valid <c>source_ref</c> (doc 25 "Trust and crossing rules"). A ref is valid when it names a
/// fact id or a <c>kind:ref</c> source key present in the input. Pure: no clock, no I/O. A bad sentence is dropped, never repaired.
/// </summary>
public static class TwinProseValidator
{
    public static ValidatedProse Validate(ReaderDraft draft, IReadOnlyList<TwinFactRecord> facts)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(facts);

        var byId = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var bySource = new Dictionary<string, List<TwinFactRecord>>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            foreach (var source in fact.SourceRefs)
            {
                if (!bySource.TryGetValue(source.Key, out var list))
                {
                    bySource[source.Key] = list = [];
                }

                list.Add(fact);
            }
        }

        var kept = new List<ValidatedSentence>();
        var dropped = 0;
        foreach (var sentence in draft.Sentences)
        {
            var text = Collapse(sentence.Text);
            var reference = sentence.SourceRef?.Trim();
            if (text.Length == 0 || string.IsNullOrEmpty(reference) || !IsSafe(text))
            {
                dropped++;
                continue;
            }

            if (byId.TryGetValue(reference, out var one))
            {
                kept.Add(new ValidatedSentence(text, reference, [one]));
            }
            else if (bySource.TryGetValue(reference, out var many))
            {
                kept.Add(new ValidatedSentence(text, reference, many));
            }
            else
            {
                dropped++;
            }
        }

        TwinTrust? trust = kept.Count == 0 ? null : kept.SelectMany(s => s.Facts).Max(f => f.Trust);
        return new ValidatedProse(kept, dropped, trust);
    }

    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // The page body is Markdown: no comment breakouts.
    private static bool IsSafe(string text) =>
        !text.Contains("-->", StringComparison.Ordinal) && !text.Contains("<!--", StringComparison.Ordinal);
}
