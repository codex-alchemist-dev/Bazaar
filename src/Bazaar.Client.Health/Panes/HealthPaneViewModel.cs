using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Panes;

/// <summary>
/// The project health pane (pane kind <see cref="PaneKinds.Health"/>, doc 25). The server derives the health block and
/// sends it as data; this view model only formats it. Model:
/// <c>{ "projects": [ { "projectId", "asOf" (yyyy-MM-dd), "status": "healthy" | "alert" | "noData", "reasons": [ { "kind", "message" } ] } ] }</c>.
/// <c>noData</c> shows as "No data", never as healthy and never as zero. A missing required field raises
/// <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class HealthPaneViewModel : IPaneViewModel
{
    internal const string DayFormat = "yyyy-MM-dd";

    public HealthPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Project health";
        Projects = PaneValues.Array(descriptor.Model, "projects").EnumerateArray().Select(ReadProject).ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    public IReadOnlyList<HealthProjectRow> Projects { get; }

    private static HealthProjectRow ReadProject(JsonElement project)
    {
        if (project.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each project must be an object");
        }

        var status = PaneValues.RequiredString(project, "status");
        var label = status switch
        {
            "healthy" => "Healthy",
            "alert" => "Alert",
            "noData" => "No data",
            _ => throw new PaneModelException($"unknown health status '{status}'"),
        };

        var asOfText = PaneValues.RequiredString(project, "asOf");
        if (!DateOnly.TryParseExact(asOfText, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
        {
            throw new PaneModelException($"'asOf' must be a yyyy-MM-dd date, got '{asOfText}'");
        }

        var reasons = PaneValues.Array(project, "reasons").EnumerateArray().Select(ReadReason).ToArray();
        return new HealthProjectRow(PaneValues.RequiredString(project, "projectId"), asOf, status, label, reasons);
    }

    private static HealthReasonRow ReadReason(JsonElement reason)
    {
        if (reason.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each reason must be an object");
        }

        return new HealthReasonRow(PaneValues.RequiredString(reason, "kind"), PaneValues.RequiredString(reason, "message"));
    }
}

/// <summary>One project's health row. <see cref="StatusLabel"/> is the text shown for <see cref="Status"/>.</summary>
public sealed record HealthProjectRow(
    string ProjectId,
    DateOnly AsOf,
    string Status,
    string StatusLabel,
    IReadOnlyList<HealthReasonRow> Reasons)
{
    /// <summary>The status line, for example "Alert as of 2026-11-17".</summary>
    public string Line => $"{StatusLabel} as of {AsOf.ToString(HealthPaneViewModel.DayFormat, CultureInfo.InvariantCulture)}";
}

/// <summary>One reason behind a project's status.</summary>
public sealed record HealthReasonRow(string Kind, string Message);
