using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>list</c> tool: the files and directories under a worktree directory, to a depth cap (doc 17: Read). Arguments: <c>path</c>
/// (default: the worktree root), <c>depth</c> (1 to 8, default 1) and <c>max_entries</c> (1 to 2000, default 500). Directory entries
/// are derived from the file paths the store lists. Every candidate entry is resolved through the store, and one that resolves
/// outside the worktree is omitted. Names are repository content and are returned as data only, flagged untrusted.
/// </summary>
public sealed class ListToolHandler(IWorktreeFilesSource source, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "list";

    private const int _maxPath = 1024;
    private const int _maxDepth = 8;
    private const int _defaultDepth = 1;
    private const int _maxEntries = 2000;
    private const int _defaultEntries = 500;

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

        string? path;
        int depth;
        int maxEntries;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "path", "depth", "max_entries");
            path = args.OptionalString("path", _maxPath, allowEmpty: true);
            depth = args.OptionalInt("depth", 1, _maxDepth, _defaultDepth);
            maxEntries = args.OptionalInt("max_entries", 1, _maxEntries, _defaultEntries);
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

        // The worktree root is the only path that skips the jail; every other directory is jailed like a file.
        var directory = "";
        if (!string.IsNullOrEmpty(path) && path != ".")
        {
            var jail = await PathJail.AuthorizeAsync(files, path, cancellationToken).ConfigureAwait(false);
            if (!jail.Allowed)
            {
                return ReadToolHandler.Refused(jail);
            }

            directory = jail.Path;
        }

        IReadOnlyList<string> listed;
        try
        {
            listed = await files.ListAsync(directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CodingToolResults.Failure("list_failed", "The directory could not be listed, so no entries are returned.");
        }

        var candidates = Candidates(listed, directory, depth);
        var entries = new List<(string Path, bool IsDirectory)>();
        var omitted = 0;
        var truncated = false;
        foreach (var candidate in candidates)
        {
            if (entries.Count == maxEntries)
            {
                truncated = true;
                break;
            }

            // An entry that does not resolve inside the worktree (a link out, or a link the store cannot follow) is never listed.
            var real = await files.ResolveRealPathAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
            if (real is null)
            {
                omitted++;
                continue;
            }

            entries.Add(candidate);
        }

        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteString("path", directory);
            w.WriteStartArray("entries");
            foreach (var entry in entries)
            {
                w.WriteStartObject();
                w.WriteString("path", entry.Path);
                w.WriteString("type", entry.IsDirectory ? "directory" : "file");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteBoolean("truncated", truncated);
            w.WriteNumber("omitted_escaping", omitted);
            w.WriteBoolean("content_is_untrusted", true);
        }));
    }

    /// <summary>
    /// Turns the store's recursive file list into entries under <paramref name="directory"/>, within <paramref name="depth"/>: each
    /// file at or above the depth cap, and each directory on the way to one. Sorted ordinally, without duplicates.
    /// </summary>
    private static List<(string Path, bool IsDirectory)> Candidates(IReadOnlyList<string> listed, string directory, int depth)
    {
        var prefix = directory.Length == 0 ? "" : directory + "/";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string Path, bool IsDirectory)>();
        foreach (var file in listed)
        {
            if (!file.StartsWith(prefix, StringComparison.Ordinal) || file.Length == prefix.Length)
            {
                continue;
            }

            var segments = file[prefix.Length..].Split('/');
            for (var level = 1; level <= Math.Min(segments.Length - 1, depth); level++)
            {
                var dir = prefix + string.Join('/', segments.Take(level));
                if (seen.Add(dir))
                {
                    result.Add((dir, true));
                }
            }

            if (segments.Length <= depth && seen.Add(file))
            {
                result.Add((file, false));
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }
}
