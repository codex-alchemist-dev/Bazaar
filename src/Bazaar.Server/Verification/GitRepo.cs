namespace Bazaar.Server.Verification;

public sealed record ChangedFile(char Status, string Path);

public sealed record AddedLine(string Path, string Text);

/// <summary>Thin git CLI wrapper used by the gate. Every call goes through <see cref="IProcessRunner"/>.</summary>
public sealed class GitRepo(IProcessRunner runner, string directory)
{
    private static readonly TimeSpan _gitTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(params string[] args)
    {
        var result = await runner.RunAsync("git", args, directory, _gitTimeout);
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {directory}: {result.Output}");
        }

        return result.Output;
    }

    public async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string baseRef, string headRef)
    {
        var output = await RunAsync("diff", "--name-status", "--no-renames", baseRef, headRef);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('\t', 2))
            .Where(p => p.Length == 2)
            .Select(p => new ChangedFile(p[0][0], p[1]))
            .ToList();
    }

    public async Task<IReadOnlyList<AddedLine>> AddedLinesAsync(string baseRef, string headRef)
    {
        var output = await RunAsync("diff", "-U0", "--no-renames", baseRef, headRef);
        var lines = new List<AddedLine>();
        var path = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = line.StartsWith("+++ b/", StringComparison.Ordinal) ? line[6..] : "";
            }
            else if (line.StartsWith('+') && path.Length > 0)
            {
                lines.Add(new AddedLine(path, line[1..]));
            }
        }

        return lines;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string gitRef)
    {
        var output = await RunAsync("ls-tree", "-r", "--name-only", gitRef);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task<string> ShowAsync(string gitRef, string path) => RunAsync("show", $"{gitRef}:{path}");
}
