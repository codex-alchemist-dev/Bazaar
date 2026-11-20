using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.CompanyMemory.Panes;

/// <summary>
/// The company memory decision chain pane (pane kind <see cref="PaneKinds.CompanyMemory"/>, doc 25). The server derives the
/// chain from the Vault decision records and sends it as data; this view model only orders and formats it. Read only.
/// Model: <c>{ "decisions": [ decision ], "links": [ link ] }</c>, where a decision is
/// <c>{ "path", "title", "status": "proposed" | "accepted" | "superseded", "decidedIn" }</c> and a link is
/// <c>{ "fromDoc", "toDoc", "relation", "status", "fromMissing", "toMissing" }</c>. <c>decidedIn</c> is the GC message that
/// decided the record, or null. A link's <c>status</c> is the status of <c>fromDoc</c>, or null when that record is missing.
/// A missing required field, a non-string where a string is needed or an unknown status raises
/// <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class CompanyMemoryPaneViewModel : IPaneViewModel
{
    private static readonly string[] _decisionStatuses = ["proposed", "accepted", "superseded"];

    public CompanyMemoryPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Company memory · Decision chain";

        var model = descriptor.Model;
        Decisions = PaneValues.Array(model, "decisions").EnumerateArray()
            .Select(ReadDecision)
            .OrderBy(decision => decision.Path, StringComparer.Ordinal)
            .ToArray();
        Links = PaneValues.Array(model, "links").EnumerateArray()
            .Select(ReadLink)
            .OrderBy(link => link.FromDoc, StringComparer.Ordinal)
            .ThenBy(link => link.ToDoc, StringComparer.Ordinal)
            .ThenBy(link => link.Relation, StringComparer.Ordinal)
            .ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>Decision records, ordered by Vault path (ordinal). Superseded records keep their row and say so.</summary>
    public IReadOnlyList<CompanyMemoryDecisionRow> Decisions { get; }

    /// <summary>Chain edges, ordered by the superseding record, then the superseded one, then relation (ordinal).</summary>
    public IReadOnlyList<CompanyMemoryLinkRow> Links { get; }

    private static CompanyMemoryDecisionRow ReadDecision(JsonElement decision)
    {
        if (decision.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each decision must be an object");
        }

        return new CompanyMemoryDecisionRow(
            PaneValues.RequiredString(decision, "path"),
            PaneValues.RequiredString(decision, "title"),
            RequiredStatus(decision, "status", _decisionStatuses),
            NullableString(decision, "decidedIn"));
    }

    private static CompanyMemoryLinkRow ReadLink(JsonElement link)
    {
        if (link.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each link must be an object");
        }

        var status = NullableString(link, "status");
        if (status is not null && Array.IndexOf(_decisionStatuses, status) < 0)
        {
            throw new PaneModelException($"unknown decision status '{status}'");
        }

        return new CompanyMemoryLinkRow(
            PaneValues.RequiredString(link, "fromDoc"),
            PaneValues.RequiredString(link, "toDoc"),
            PaneValues.RequiredString(link, "relation"),
            status,
            PaneValues.RequiredBool(link, "fromMissing"),
            PaneValues.RequiredBool(link, "toMissing"));
    }

    private static string RequiredStatus(JsonElement parent, string name, string[] allowed)
    {
        var status = PaneValues.RequiredString(parent, name);
        if (Array.IndexOf(allowed, status) < 0)
        {
            throw new PaneModelException($"unknown decision status '{status}'");
        }

        return status;
    }

    /// <summary>A field that must be present, holding a string or null.</summary>
    private static string? NullableString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new PaneModelException($"model field '{name}' is missing");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString()!,
            _ => throw new PaneModelException($"model field '{name}' must be a string or null"),
        };
    }
}

/// <summary>One decision record row. <see cref="Line"/> is the text the view shows; superseded rows carry the marker.</summary>
public sealed record CompanyMemoryDecisionRow(string Path, string Title, string Status, string? DecidedIn)
{
    public bool IsSuperseded => string.Equals(Status, "superseded", StringComparison.Ordinal);

    public string Line => $"{Title} [{Status}]" + (IsSuperseded ? " · replaced by a newer decision" : string.Empty);

    /// <summary>The GC message that decided the record, or a note that none is recorded.</summary>
    public string DecidedText => DecidedIn is null ? "no decision message recorded" : $"decided in {DecidedIn}";
}

/// <summary>
/// One chain edge. The edge is always shown. An end that is missing is listed in <see cref="MissingSources"/> as its own
/// "missing source" row, so an unresolved reference never hides the link.
/// </summary>
public sealed record CompanyMemoryLinkRow(
    string FromDoc,
    string ToDoc,
    string Relation,
    string? Status,
    bool FromMissing,
    bool ToMissing)
{
    /// <summary>The edge read as "newer record relation older record".</summary>
    public string Line => $"{FromDoc} {Relation} {ToDoc}";

    /// <summary>One "missing source" text per end that the server could not resolve; empty when both ends resolve.</summary>
    public IReadOnlyList<string> MissingSources
    {
        get
        {
            var sources = new List<string>(2);
            if (FromMissing)
            {
                sources.Add($"missing source: {FromDoc}");
            }

            if (ToMissing)
            {
                sources.Add($"missing source: {ToDoc}");
            }

            return sources;
        }
    }
}
