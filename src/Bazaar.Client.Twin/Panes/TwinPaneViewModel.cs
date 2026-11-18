using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Twin.Panes;

/// <summary>
/// The Digital Twin project card (pane kind <see cref="PaneKinds.Twin"/>, doc 25). The server derives the facts and prose
/// pages and sends them as data; this view model only formats them. Field names are camelCase and copied from doc 25:
/// <c>{ "projects": [ { "projectId", "title", "facts": [ fact ], "pages": [ { "page", "sentences": [ { "text", "sourceRef" } ] } ] } ] }</c>,
/// where a fact is <c>{ "factId", "kind", "subject", "value", "confidence", "status", "trust", "originKind", "sourceRefs": [ { "kind", "ref", "commit" } ] }</c>.
/// A missing required field raises <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// A sentence or fact with no source is shown as an explicit "missing source" row, never as a blank.
/// </summary>
public sealed class TwinPaneViewModel : IPaneViewModel
{
    private static readonly string[] _statuses = ["active", "stale", "superseded"];

    public TwinPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Digital Twin";

        Projects = PaneValues.Array(descriptor.Model, "projects").EnumerateArray().Select(ReadProject).ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    public IReadOnlyList<TwinProjectRow> Projects { get; }

    private static TwinProjectRow ReadProject(JsonElement project)
    {
        if (project.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each twin project must be an object");
        }

        var facts = PaneValues.Array(project, "facts").EnumerateArray().Select(ReadFact).ToArray();
        var pages = PaneValues.Array(project, "pages").EnumerateArray().Select(ReadPage).ToArray();
        return new TwinProjectRow(
            PaneValues.RequiredString(project, "projectId"),
            PaneValues.RequiredString(project, "title"),
            facts,
            pages);
    }

    private static TwinFactRow ReadFact(JsonElement fact)
    {
        if (fact.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each twin fact must be an object");
        }

        var status = PaneValues.RequiredString(fact, "status");
        if (Array.IndexOf(_statuses, status) < 0)
        {
            throw new PaneModelException($"unknown twin fact status '{status}'");
        }

        return new TwinFactRow(
            PaneValues.RequiredString(fact, "factId"),
            PaneValues.RequiredString(fact, "kind"),
            PaneValues.RequiredString(fact, "subject"),
            ReadValue(fact, "value"),
            PaneValues.Scalar(fact, "confidence"),
            status,
            PaneValues.Scalar(fact, "trust"),
            PaneValues.RequiredString(fact, "originKind"),
            ReadSourceRefs(fact));
    }

    private static List<TwinSourceRefRow> ReadSourceRefs(JsonElement fact)
    {
        var refs = new List<TwinSourceRefRow>();
        foreach (var item in PaneValues.Array(fact, "sourceRefs").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new PaneModelException("each source ref must be an object");
            }

            refs.Add(new TwinSourceRefRow(
                PaneValues.RequiredString(item, "kind"),
                PaneValues.RequiredString(item, "ref"),
                ReadNullableString(item, "commit")));
        }

        return refs;
    }

    private static TwinPageRow ReadPage(JsonElement page)
    {
        if (page.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each twin page must be an object");
        }

        var sentences = PaneValues.Array(page, "sentences").EnumerateArray().Select(ReadSentence).ToArray();
        return new TwinPageRow(PaneValues.RequiredString(page, "page"), sentences);
    }

    private static TwinSentenceRow ReadSentence(JsonElement sentence)
    {
        if (sentence.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each twin sentence must be an object");
        }

        return new TwinSentenceRow(
            PaneValues.RequiredString(sentence, "text"),
            ReadNullableString(sentence, "sourceRef"));
    }

    /// <summary>A key that must be present; its value may be null, which the view reads as "no source".</summary>
    private static string? ReadNullableString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new PaneModelException($"model field '{name}' is missing");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new PaneModelException($"model field '{name}' must be a string or null"),
        };
    }

    /// <summary>The fact value shown as text. Any JSON type is allowed, so the key must exist and null shows as empty.</summary>
    private static string ReadValue(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new PaneModelException($"model field '{name}' is missing");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => value.GetString()!,
            _ => value.GetRawText(),
        };
    }
}

/// <summary>One project card: its title, facts and prose pages.</summary>
public sealed record TwinProjectRow(
    string ProjectId,
    string Title,
    IReadOnlyList<TwinFactRow> Facts,
    IReadOnlyList<TwinPageRow> Pages);

/// <summary>One fact with its confidence, trust, origin and source refs. A stale fact keeps its status and is labelled stale.</summary>
public sealed record TwinFactRow(
    string FactId,
    string Kind,
    string Subject,
    string Value,
    string Confidence,
    string Status,
    string Trust,
    string OriginKind,
    IReadOnlyList<TwinSourceRefRow> SourceRefs)
{
    /// <summary>True when the server marked the fact stale. The view shows a visible stale label.</summary>
    public bool IsStale => string.Equals(Status, "stale", StringComparison.Ordinal);

    /// <summary>The headline: kind, subject and value.</summary>
    public string Line => $"{Kind}: {Subject} = {Value}";

    /// <summary>Confidence, trust and origin on one line.</summary>
    public string Meta => $"confidence {Confidence} · trust {Trust} · origin {OriginKind}";
}

/// <summary>One source a fact cites. <see cref="Commit"/> is null when the source has no commit.</summary>
public sealed record TwinSourceRefRow(string Kind, string Ref, string? Commit)
{
    public string Text => Commit is { Length: > 0 } commit ? $"{Kind} {Ref} @ {commit}" : $"{Kind} {Ref}";
}

/// <summary>One prose page: its name and sentences.</summary>
public sealed record TwinPageRow(string Page, IReadOnlyList<TwinSentenceRow> Sentences);

/// <summary>One prose sentence. A null or blank <see cref="SourceRef"/> means the sentence has no source.</summary>
public sealed record TwinSentenceRow(string Text, string? SourceRef)
{
    public bool HasSource => !string.IsNullOrWhiteSpace(SourceRef);
}
