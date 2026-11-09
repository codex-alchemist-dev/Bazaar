using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Agents.Turns.Tools.Db;

/// <summary>
/// Pure mapping between <see cref="SessionTaint"/> and the <c>session_taint</c> columns (agent_turns migration 0001). No I/O,
/// so the row shape is testable without PostgreSQL.
/// </summary>
public static class SessionTaintMapping
{
    /// <summary>The <c>state</c> column text for a level. Matches the CHECK list in the migration.</summary>
    public static string ToSqlState(TaintState state) => state switch
    {
        TaintState.Clean => "Clean",
        TaintState.Soft => "Soft",
        TaintState.Hard => "Hard",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown taint state."),
    };

    /// <summary>Reads the <c>state</c> column text back to a level.</summary>
    public static TaintState FromSqlState(string value) => value switch
    {
        "Clean" => TaintState.Clean,
        "Soft" => TaintState.Soft,
        "Hard" => TaintState.Hard,
        _ => throw new InvalidOperationException($"session_taint row has unknown state '{value}'."),
    };

    /// <summary>Maps the latest row (state and sources columns) to the session's taint.</summary>
    public static SessionTaint MapRow(string state, string sourcesJson)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sourcesJson);

        var rows = JsonSerializer.Deserialize(sourcesJson, SessionTaintJsonContext.Default.SessionTaintSourceRowArray)
            ?? throw new InvalidOperationException("session_taint sources is not a JSON array.");

        var builder = ImmutableArray.CreateBuilder<TaintSource>(rows.Length);
        foreach (var row in rows)
        {
            builder.Add(new TaintSource(
                row.SourceId,
                Enum.Parse<ContentTrust>(row.Trust, ignoreCase: false),
                Enum.Parse<TaintState>(row.Raised, ignoreCase: false)));
        }

        return new SessionTaint(FromSqlState(state), builder.MoveToImmutable());
    }

    /// <summary>The <c>sources</c> column JSON for a session's sources, in arrival order.</summary>
    public static string SerializeSources(ImmutableArray<TaintSource> sources)
    {
        var rows = new SessionTaintSourceRow[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var s = sources[i];
            rows[i] = new SessionTaintSourceRow(s.SourceId, s.Trust.ToString(), s.Raised.ToString());
        }

        return JsonSerializer.Serialize(rows, SessionTaintJsonContext.Default.SessionTaintSourceRowArray);
    }
}

/// <summary>One source as stored in the <c>sources</c> JSON array.</summary>
internal sealed record SessionTaintSourceRow(Guid SourceId, string Trust, string Raised);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SessionTaintSourceRow[]))]
internal sealed partial class SessionTaintJsonContext : JsonSerializerContext
{
}
