using System.Text;

namespace Bazaar.Server.RepoMap;

/// <summary>
/// Fake <see cref="IRepoWorkspace"/> for tests and for building the repo map before a VM exists.
/// It answers <c>rg --files</c> and literal <c>rg --regexp=</c> searches over its files, ignoring globs so tests can check the callers' own filters.
/// It records every argv. A hook set in <see cref="OnRun"/> replaces the simulation.
/// Any other argv exits 127, so an unexpected command shows up in the result.
/// </summary>
public sealed class InMemoryRepoWorkspace : IRepoWorkspace
{
    private readonly object _gate = new();
    private readonly SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly List<IReadOnlyList<string>> _runs = [];

    /// <summary>Replaces the simulation for the next calls when set.</summary>
    public Func<IReadOnlyList<string>, WorkspaceOutput>? OnRun { get; set; }

    /// <summary>Every argv received, in arrival order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>Adds a UTF-8 text file.</summary>
    public void AddText(string path, string content) => AddBytes(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Adds a file with raw bytes.</summary>
    public void AddBytes(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            _files[path] = bytes;
        }
    }

    /// <inheritdoc />
    public Task<WorkspaceOutput> RunAsync(IReadOnlyList<string> argv, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _runs.Add([.. argv]);
        }

        var output = OnRun is { } hook ? hook(argv) : Simulate(argv);
        return Task.FromResult(output);
    }

    /// <inheritdoc />
    public Task<byte[]?> ReadFileAsync(string relativePath, int maxBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_files.TryGetValue(relativePath, out var bytes) || bytes.Length > maxBytes)
            {
                return Task.FromResult<byte[]?>(null);
            }

            return Task.FromResult<byte[]?>([.. bytes]);
        }
    }

    private WorkspaceOutput Simulate(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || argv[0] != "rg")
        {
            return new WorkspaceOutput(127, TimedOut: false, "not scripted: " + string.Join(' ', argv), OutputTruncated: false);
        }

        KeyValuePair<string, byte[]>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _files];
        }

        if (argv.Contains("--files"))
        {
            var listing = new StringBuilder();
            foreach (var (path, _) in snapshot)
            {
                listing.Append(path).Append('\0');
            }

            return new WorkspaceOutput(0, TimedOut: false, listing.ToString(), OutputTruncated: false);
        }

        var pattern = argv.FirstOrDefault(a => a.StartsWith("--regexp=", StringComparison.Ordinal));
        if (pattern is null)
        {
            return new WorkspaceOutput(2, TimedOut: false, "no pattern", OutputTruncated: false);
        }

        var needle = pattern["--regexp=".Length..];
        var comparison = argv.Contains("--ignore-case") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var stdout = new StringBuilder();
        foreach (var (path, bytes) in snapshot)
        {
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(needle, comparison))
                {
                    stdout.Append(path).Append('\0').Append(i + 1).Append(':').Append(lines[i].TrimEnd('\r')).Append('\n');
                }
            }
        }

        return new WorkspaceOutput(stdout.Length == 0 ? 1 : 0, TimedOut: false, stdout.ToString(), OutputTruncated: false);
    }
}
