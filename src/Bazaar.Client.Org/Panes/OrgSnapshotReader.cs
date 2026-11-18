using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Org.Panes;

/// <summary>Parses the Org pane model into an <see cref="OrgSnapshot"/>; any shape mismatch raises <see cref="PaneModelException"/>.</summary>
internal static class OrgSnapshotReader
{
    internal static OrgSnapshot Read(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("the Org model must be an object");
        }

        var projects = PaneValues.Array(model, "projects").EnumerateArray().Select(ReadProject).ToArray();
        var agents = PaneValues.Array(model, "agents").EnumerateArray().Select(ReadAgent).ToArray();
        var edges = PaneValues.Array(model, "edges").EnumerateArray().Select(ReadEdge).ToArray();
        return new OrgSnapshot(projects, agents, edges);
    }

    private static OrgProjectSummary ReadProject(JsonElement project)
    {
        RequireObject(project, "project");
        return new OrgProjectSummary(PaneValues.RequiredString(project, "id"), PaneValues.RequiredString(project, "name"));
    }

    private static OrgAgentSummary ReadAgent(JsonElement agent)
    {
        RequireObject(agent, "agent");
        return new OrgAgentSummary(PaneValues.RequiredString(agent, "id"), PaneValues.RequiredString(agent, "name"));
    }

    private static OrgEdgeSummary ReadEdge(JsonElement edge)
    {
        RequireObject(edge, "edge");
        var kind = PaneValues.RequiredString(edge, "kind") switch
        {
            "reports_to" => OrgEdgeKind.ReportsTo,
            "lent_to" => OrgEdgeKind.LentTo,
            var other => throw new PaneModelException($"unknown Org edge kind '{other}'"),
        };

        var starts = ReadDate(edge, "startsAt") ?? throw new PaneModelException("model field 'startsAt' must be a date");
        return new OrgEdgeSummary(
            PaneValues.RequiredString(edge, "id"),
            kind,
            PaneValues.RequiredString(edge, "fromAgentId"),
            PaneValues.RequiredString(edge, "toAgentId"),
            ReadOptionalString(edge, "projectId"),
            starts,
            ReadDate(edge, "endsAt"));
    }

    private static string? ReadOptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return PaneValues.RequiredString(parent, name);
    }

    private static DateTimeOffset? ReadDate(JsonElement parent, string name)
    {
        var text = ReadOptionalString(parent, name);
        if (text is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : throw new PaneModelException($"model field '{name}' must be a date");
    }

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException($"each {what} must be an object");
        }
    }
}
