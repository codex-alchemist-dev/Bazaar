namespace Bazaar.Client.Athanor.Hosting;

/// <summary>The editor hosts a head can offer (doc 57 §2.2). <see cref="Auto"/> is only a preference, never a candidate.</summary>
public enum EditorHostKind
{
    Auto,
    WebView2,
    Gtk,
    AvaloniaEdit,
    PlainText,
}

/// <summary>One host a head offers: whether it can run here, and how to build it. Both run on the UI thread.</summary>
public sealed record EditorHostCandidate(EditorHostKind Kind, Func<bool> IsAvailable, Func<IEditorHost> Create);

/// <summary>The host the selector picked, and why the earlier ones in the order were not used.</summary>
public sealed record EditorHostChoice(IEditorHost Host, EditorHostKind Kind, IReadOnlyList<string> Skipped);

/// <summary>
/// Chooses the editor host for a head (doc 57 §2.2): an ordered preference over the candidates the head offers. Pure
/// logic; the head supplies the platform checks and factories. The plain-text host always ends the chain.
/// </summary>
public static class EditorHostSelector
{
    /// <summary>Parses the operator setting. Null, blank or <c>auto</c> is <see cref="EditorHostKind.Auto"/>; unknown text is not valid.</summary>
    public static bool TryParse(string? text, out EditorHostKind kind)
    {
        kind = EditorHostKind.Auto;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "auto":
                return true;
            case "webview2":
                kind = EditorHostKind.WebView2;
                return true;
            case "gtk":
                kind = EditorHostKind.Gtk;
                return true;
            case "avaloniaedit":
                kind = EditorHostKind.AvaloniaEdit;
                return true;
            case "plain":
            case "plaintext":
                kind = EditorHostKind.PlainText;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The order to try: the preferred kind first, then the platform default order without it. Always ends with plain text.</summary>
    public static IReadOnlyList<EditorHostKind> Order(bool windows, EditorHostKind preference)
    {
        var order = new List<EditorHostKind>();
        if (preference != EditorHostKind.Auto)
        {
            order.Add(preference);
        }

        foreach (var kind in windows
                     ? new[] { EditorHostKind.WebView2, EditorHostKind.AvaloniaEdit, EditorHostKind.PlainText }
                     : [EditorHostKind.AvaloniaEdit, EditorHostKind.PlainText])
        {
            if (!order.Contains(kind))
            {
                order.Add(kind);
            }
        }

        if (!order.Contains(EditorHostKind.PlainText))
        {
            order.Add(EditorHostKind.PlainText);
        }

        return order;
    }

    /// <summary>Walks <paramref name="order"/> and returns the first candidate that is available and builds.</summary>
    public static EditorHostChoice Select(IEnumerable<EditorHostKind> order, IEnumerable<EditorHostCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(candidates);

        var offered = candidates.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.First());
        var skipped = new List<string>();
        foreach (var kind in order)
        {
            if (!offered.TryGetValue(kind, out var candidate))
            {
                if (kind == EditorHostKind.PlainText)
                {
                    break;
                }

                skipped.Add($"{kind}: not offered by this head");
                continue;
            }

            if (!Probe(candidate, out var reason))
            {
                skipped.Add($"{kind}: {reason}");
                continue;
            }

            try
            {
                return new EditorHostChoice(candidate.Create(), kind, skipped);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                skipped.Add($"{kind}: could not start ({ex.Message})");
            }
        }

        return new EditorHostChoice(new PlainTextFallbackHost(), EditorHostKind.PlainText, skipped);
    }

    private static bool Probe(EditorHostCandidate candidate, out string reason)
    {
        reason = "unavailable";
        try
        {
            return candidate.IsAvailable();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = $"availability check failed ({ex.Message})";
            return false;
        }
    }
}
