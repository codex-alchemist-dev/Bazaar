namespace Bazaar.Server.CodingEdit;

/// <summary>
/// Chooses the edit tool for an agent's <see cref="EditFormat"/> and records each outcome in <see cref="EditMetrics"/>. Every edit
/// goes through here, so the metrics see every outcome.
/// </summary>
public sealed class EditToolRouter
{
    private readonly Dictionary<EditFormat, IEditTool> _tools;
    private readonly EditMetrics _metrics;

    /// <summary>Builds the router. Each edit format must have exactly one tool.</summary>
    public EditToolRouter(IEnumerable<IEditTool> tools, EditMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _tools = [];
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Format, tool))
            {
                throw new ArgumentException($"More than one edit tool implements {tool.Format}.", nameof(tools));
            }
        }
    }

    /// <summary>Applies one edit with the tool for <paramref name="agentFormat"/>. An unknown format is refused, not guessed.</summary>
    public async Task<EditResult> ApplyAsync(EditFormat agentFormat, EditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _tools.TryGetValue(agentFormat, out var tool)
            ? await tool.ApplyAsync(request, ct)
            : EditResult.Refused(EditRejectReason.Malformed, request.Path, "no edit tool is registered for this agent's edit format");
        _metrics.Record(result);
        return result;
    }
}
