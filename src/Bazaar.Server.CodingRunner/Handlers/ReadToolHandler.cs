using System.Text;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>read</c> tool: the lines of one worktree file (doc 17: Read). Arguments: <c>path</c> (required), <c>start_line</c> (1-based,
/// default 1) and <c>max_lines</c> (1 to 1000, default 200). The path goes through <see cref="PathJail"/>. Files over 1 MiB and files
/// with a NUL byte are refused. The text is repository content and is returned as data only, flagged untrusted.
/// </summary>
public sealed class ReadToolHandler(IWorktreeFilesSource source, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "read";

    /// <summary>Largest file the tool will load, in bytes.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>Most characters of file text returned to the model.</summary>
    public const int MaxOutputCharacters = 200_000;

    private const int _maxPath = 1024;
    private const int _maxStartLine = 1_000_000;
    private const int _maxLines = 1000;
    private const int _defaultLines = 200;

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Read;

    /// <inheritdoc />
    public bool WritesWorktree => false;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        string path;
        int startLine;
        int maxLines;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "path", "start_line", "max_lines");
            path = args.RequiredString("path", _maxPath);
            startLine = args.OptionalInt("start_line", 1, _maxStartLine, 1);
            maxLines = args.OptionalInt("max_lines", 1, _maxLines, _defaultLines);
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        var worktree = GitToolSupport.Resolve(toolCall, host, out var error);
        if (worktree is null)
        {
            return error!;
        }

        var files = source.FilesFor(worktree);
        if (files is null)
        {
            return CodingToolResults.Failure("worktree_files_unavailable", "No file store is available for this worktree.");
        }

        var jail = await PathJail.AuthorizeAsync(files, path, cancellationToken).ConfigureAwait(false);
        if (!jail.Allowed)
        {
            return Refused(jail);
        }

        WorktreeFile? file;
        try
        {
            file = await files.ReadAsync(jail.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The store's message can carry guest paths, so only the fixed text reaches the model.
            return CodingToolResults.Failure("read_failed", "The file could not be read, so no content is returned.");
        }

        if (file is null)
        {
            return CodingToolResults.Failure("file_not_found", "No file exists at that path in this worktree.");
        }

        if (file.Content.Length > MaxFileBytes)
        {
            return CodingToolResults.Failure("file_too_large", "The file is larger than the read limit of 1 MiB.");
        }

        if (file.Content.AsSpan().Contains((byte)0))
        {
            return CodingToolResults.Failure("binary_file", "The file contains binary data, so it is not returned as text.");
        }

        var lines = SplitLines(Encoding.UTF8.GetString(file.Content));
        var total = lines.Count;
        var sb = new StringBuilder();
        var returned = 0;
        var truncated = false;
        for (var index = startLine - 1; index < total && returned < maxLines; index++)
        {
            var line = lines[index];
            if (sb.Length + line.Length + 1 > MaxOutputCharacters)
            {
                // A single line longer than the cap is cut; the cut is reported as truncation.
                if (returned == 0)
                {
                    sb.Append(line, 0, Math.Min(line.Length, MaxOutputCharacters));
                    returned = 1;
                }

                truncated = true;
                break;
            }

            sb.Append(line).Append('\n');
            returned++;
        }

        if (startLine - 1 + returned < total)
        {
            truncated = true;
        }

        var content = sb.ToString();
        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteString("path", jail.Path);
            w.WriteNumber("total_lines", total);
            w.WriteNumber("start_line", startLine);
            w.WriteNumber("returned_lines", returned);
            w.WriteBoolean("truncated", truncated);
            w.WriteString("content", content);
            w.WriteBoolean("content_is_untrusted", true);
        }));
    }

    /// <summary>Maps a jail refusal to a fixed, non-echoing error. Lexical refusals are usage errors; escapes are failures.</summary>
    internal static CodingToolOutput Refused(JailDecision jail) =>
        jail.Reason == EditRejectReason.Malformed
            ? CodingToolResults.Failure(CodingToolResults.MalformedCode, "Argument 'path' is not a worktree file path.", CodingToolResults.MalformedExitCode)
            : CodingToolResults.Failure("path_escape", "The path leaves the worktree or cannot be resolved inside it, so it is refused.");

    /// <summary>Splits text into lines without their terminators. A final newline does not start an extra line.</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
