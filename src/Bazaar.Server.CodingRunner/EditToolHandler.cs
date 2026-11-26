using System.Text;
using System.Text.Json;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner;

/// <summary>
/// The <c>edit</c> tool over <see cref="EditToolRouter"/> (doc 17: edit is Read in the worktree). The agent's edit format
/// comes from the <c>formatFor</c> function (its whitelist), never from the model's arguments. A refused edit exits 1.
/// </summary>
public sealed class EditToolHandler : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "edit";

    private readonly EditToolRouter _router;
    private readonly Func<string, EditFormat> _formatFor;

    /// <summary>Creates the handler. <paramref name="formatFor"/> maps an agent id to its edit format.</summary>
    public EditToolHandler(EditToolRouter router, Func<string, EditFormat> formatFor)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(formatFor);
        _router = router;
        _formatFor = formatFor;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Read;

    /// <inheritdoc />
    public bool WritesWorktree => true;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var format = _formatFor(toolCall.AgentId);
        var path = Str(toolCall.Args, "path") ?? string.Empty;
        EditRequest? request = format switch
        {
            EditFormat.WholeFile when Str(toolCall.Args, "content") is { } content => new WholeFileEditRequest(path, content),
            EditFormat.LineAnchored when Int(toolCall.Args, "start_line") is { } s
                && Int(toolCall.Args, "end_line") is { } e
                && Str(toolCall.Args, "expected_lines_sha256") is { } h
                && Str(toolCall.Args, "replacement") is { } r => new LineAnchoredEditRequest(path, s, e, h, r),
            EditFormat.SearchReplace when Str(toolCall.Args, "search") is { } search
                && Str(toolCall.Args, "replace") is { } replace => new SearchReplaceEditRequest(path, search, replace),
            _ => null,
        };

        var result = request is null
            ? EditResult.Refused(EditRejectReason.Malformed, path, "a required argument is missing or has the wrong type for this agent's edit format")
            : await _router.ApplyAsync(format, request, cancellationToken).ConfigureAwait(false);

        return new CodingToolOutput(new ExecOutcome(result.Applied ? 0 : 1), Serialise(result));
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string Serialise(EditResult r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteBoolean("applied", r.Applied);
            w.WriteString("reason", r.Reason?.ToString());
            w.WriteString("path", r.Path);
            w.WriteString("before_sha256", r.BeforeSha256);
            w.WriteString("after_sha256", r.AfterSha256);
            w.WriteNumber("changed_lines", r.ChangedLines);
            w.WriteString("detail", r.Detail);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
