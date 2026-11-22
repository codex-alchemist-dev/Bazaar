using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Gateway.Selection;

/// <summary>Reads and writes the tracker's snapshot as a small JSON file. Counters only; never a prompt or a secret.</summary>
public static class StatsFile
{
    public static void Save(string path, IReadOnlyList<EndpointStatsSnapshot> snapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var rows = snapshots.Select(s => new Row(s.Key.ProviderId, s.Key.ModelId, s.Successes, s.Failures, s.PeakEwmaMs)).ToList();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Document(1, rows), StatsJson.Default.Document));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The saved snapshots, or an empty list when the file is missing or unreadable: the selector then starts from priors.</summary>
    public static IReadOnlyList<EndpointStatsSnapshot> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var doc = JsonSerializer.Deserialize(File.ReadAllText(path), StatsJson.Default.Document);
            return doc is { Version: 1, Endpoints: { } rows }
                ? [.. rows.Select(r => new EndpointStatsSnapshot(new EndpointKey(r.Provider, r.Model), r.Successes, r.Failures, r.PeakEwmaMs))]
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal sealed record Document(int Version, List<Row> Endpoints);

    internal sealed record Row(string Provider, string Model, double Successes, double Failures, double? PeakEwmaMs);
}

[JsonSerializable(typeof(StatsFile.Document))]
internal sealed partial class StatsJson : JsonSerializerContext;
