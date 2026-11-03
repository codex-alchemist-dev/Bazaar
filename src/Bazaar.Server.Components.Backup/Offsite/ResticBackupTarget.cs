using System.Text.Json;
using Bazaar.Contracts.Backup;
using Bazaar.Server.Components.Backup.Processes;

namespace Bazaar.Server.Components.Backup.Offsite;

/// <summary>
/// Offsite store through the restic CLI (doc 33, <c>backup.offsite_target = restic-s3</c>). Outbound only. The repository
/// and password reach restic through the environment (for example <c>RESTIC_PASSWORD_FILE</c>), never as arguments.
/// </summary>
public sealed class ResticBackupTarget(IProcessRunner runner, string resticPath, string repository, IReadOnlyDictionary<string, string> environment, string name = "restic") : IBackupTarget
{
    public string Name => name;

    public async Task<OffsiteSnapshot> PutDirectoryAsync(string localDirectory, IReadOnlyList<string> tags, CancellationToken ct)
    {
        if (!Directory.Exists(localDirectory))
            throw new DirectoryNotFoundException("Offsite source directory is missing.");
        var args = new List<string> { "-r", repository, "backup", localDirectory, "--json" };
        foreach (var tag in tags)
            args.Add("--tag=" + tag);
        var result = await runner.RunAsync(resticPath, args, environment, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ProcessFailedException("restic backup", result.ExitCode, result.StandardError);

        // restic --json prints one summary object per run; the last line that carries snapshot_id is the new snapshot.
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("snapshot_id", out var id))
                return new OffsiteSnapshot(id.GetString() ?? throw new InvalidDataException("restic returned a null snapshot id."), DateTimeOffset.UtcNow, tags);
        }
        throw new InvalidDataException("restic output had no snapshot_id.");
    }

    public async Task<IReadOnlyList<OffsiteSnapshot>> ListAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(resticPath, ["-r", repository, "snapshots", "--json"], environment, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ProcessFailedException("restic snapshots", result.ExitCode, result.StandardError);

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var list = new List<OffsiteSnapshot>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tags = item.TryGetProperty("tags", out var t) ? t.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : [];
            list.Add(new OffsiteSnapshot(item.GetProperty("id").GetString() ?? string.Empty, item.GetProperty("time").GetDateTimeOffset(), tags));
        }
        return list.OrderByDescending(s => s.TakenAt).ToList();
    }
}
