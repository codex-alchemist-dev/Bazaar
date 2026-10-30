using System.Globalization;
using System.Text;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Notes;

/// <summary>
/// Maps front matter entries to <see cref="MemoryNote"/> and back. Top-level keys the schema does not define
/// are kept as extra fields and written back verbatim; unknown keys inside the <c>memory</c> block are rejected.
/// </summary>
internal static class MemoryNoteFields
{
    private static readonly string[] _topKeys = ["id", "type", "title", "project", "memory"];

    private static readonly string[] _memoryKeys =
        ["kind", "status", "version", "trust", "source_trust", "tainted", "origin", "derived_from", "subjects", "deprecated", "supersedes"];

    private static readonly string[] _originKeys = ["gc", "messages"];

    private static readonly string[] _deprecatedKeys = ["reason", "by", "at"];

    /// <summary>Writes the front matter lines (each ending in LF) for a note followed by its extra fields.</summary>
    public static string WriteFront(MemoryNote note, IReadOnlyList<KeyValuePair<string, FrontValue>> extras)
    {
        var entries = new List<KeyValuePair<string, FrontValue>>
        {
            Pair("id", Scalar(note.Id)),
            Pair("type", Scalar(note.Type)),
            Pair("title", Scalar(note.Title)),
        };

        if (note.Project is not null)
        {
            entries.Add(Pair("project", Scalar(note.Project)));
        }

        entries.Add(Pair("memory", MemoryBlock(note)));
        entries.AddRange(extras);

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.Append(entry.Key).Append(": ").Append(FrontMatterCodec.WriteValue(entry.Value)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Reads typed fields. Returns false with the first error found; never throws.</summary>
    public static bool TryRead(
        IReadOnlyList<KeyValuePair<string, FrontValue>> entries,
        out MemoryNote? note,
        out List<KeyValuePair<string, FrontValue>> extras,
        out NoteParseError error,
        out string detail)
    {
        var reader = new Reader();
        note = null;
        extras = entries.Where(e => Array.IndexOf(_topKeys, e.Key) < 0).ToList();
        var top = Dict(entries);

        var id = reader.Scalar(top, "id", "");
        var type = reader.Scalar(top, "type", "");
        if (type is not null && !MemoryNoteTypes.TryKindFor(type, out _))
        {
            reader.Fail(NoteParseError.InvalidField, "'type' is not a known memory note type.");
        }

        var title = reader.Scalar(top, "title", "");
        var project = reader.Scalar(top, "project", "", required: false);

        var memory = reader.Mapping(top, "memory", "");
        MemoryKind kind = default;
        MemoryNoteStatus status = default;
        long version = 0;
        MemoryTier trust = default;
        ContentTrust sourceTrust = default;
        bool tainted = false;
        MemoryOrigin? origin = null;
        MemoryDeprecation? deprecated = null;
        IReadOnlyList<string> derived = [];
        IReadOnlyList<string> subjects = [];
        IReadOnlyList<string> supersedes = [];

        if (memory is not null)
        {
            reader.RejectUnknown(memory, _memoryKeys, "memory.");
            kind = reader.Parse<MemoryKind>(memory, "kind", "memory.", MemoryNoteWire.TryParseKind);
            status = reader.Parse<MemoryNoteStatus>(memory, "status", "memory.", MemoryNoteWire.TryParseStatus);
            version = reader.Parse<long>(memory, "version", "memory.", TryParseVersion);
            trust = reader.Parse<MemoryTier>(memory, "trust", "memory.", MemoryNoteWire.TryParseTier);
            sourceTrust = reader.Parse<ContentTrust>(memory, "source_trust", "memory.", MemoryNoteWire.TryParseSourceTrust);
            tainted = reader.Parse<bool>(memory, "tainted", "memory.", TryParseBool);
            derived = reader.Strings(memory, "derived_from", "memory.");
            subjects = reader.Strings(memory, "subjects", "memory.");
            supersedes = reader.Strings(memory, "supersedes", "memory.");

            var originMap = reader.Mapping(memory, "origin", "memory.", required: false);
            if (originMap is not null)
            {
                reader.RejectUnknown(originMap, _originKeys, "memory.origin.");
                origin = new MemoryOrigin(
                    reader.Scalar(originMap, "gc", "memory.origin.", required: false),
                    reader.Strings(originMap, "messages", "memory.origin."));
            }

            var deprecatedMap = reader.Mapping(memory, "deprecated", "memory.", required: false);
            if (deprecatedMap is not null)
            {
                reader.RejectUnknown(deprecatedMap, _deprecatedKeys, "memory.deprecated.");
                deprecated = new MemoryDeprecation(
                    reader.Scalar(deprecatedMap, "reason", "memory.deprecated.") ?? string.Empty,
                    reader.Scalar(deprecatedMap, "by", "memory.deprecated.") ?? string.Empty,
                    reader.Scalar(deprecatedMap, "at", "memory.deprecated.") ?? string.Empty);
            }
        }

        if (reader.Error is { } failure)
        {
            error = failure;
            detail = reader.Detail;
            return false;
        }

        note = new MemoryNote(
            id!,
            type!,
            title!,
            project,
            kind,
            status,
            version,
            trust,
            sourceTrust,
            tainted,
            origin,
            derived,
            subjects,
            deprecated,
            supersedes);
        error = default;
        detail = string.Empty;
        return true;
    }

    private static FrontMapping MemoryBlock(MemoryNote note)
    {
        var entries = new List<KeyValuePair<string, FrontValue>>
        {
            Pair("kind", Scalar(MemoryNoteWire.KindName(note.Kind))),
            Pair("status", Scalar(MemoryNoteWire.StatusName(note.Status))),
            Pair("version", Scalar(note.Version.ToString(CultureInfo.InvariantCulture))),
            Pair("trust", Scalar(MemoryNoteWire.TierName(note.Trust))),
            Pair("source_trust", Scalar(MemoryNoteWire.SourceTrustName(note.SourceTrust))),
            Pair("tainted", Scalar(note.Tainted ? "true" : "false")),
        };

        if (note.Origin is { } origin)
        {
            var originEntries = new List<KeyValuePair<string, FrontValue>>();
            if (origin.Gc is not null)
            {
                originEntries.Add(Pair("gc", Scalar(origin.Gc)));
            }

            if (origin.Messages.Count > 0)
            {
                originEntries.Add(Pair("messages", Sequence(origin.Messages)));
            }

            entries.Add(Pair("origin", new FrontMapping(originEntries)));
        }

        if (note.DerivedFrom.Count > 0)
        {
            entries.Add(Pair("derived_from", Sequence(note.DerivedFrom)));
        }

        if (note.Subjects.Count > 0)
        {
            entries.Add(Pair("subjects", Sequence(note.Subjects)));
        }

        if (note.Deprecated is { } deprecated)
        {
            entries.Add(Pair("deprecated", new FrontMapping(
            [
                Pair("reason", Scalar(deprecated.Reason)),
                Pair("by", Scalar(deprecated.By)),
                Pair("at", Scalar(deprecated.At)),
            ])));
        }

        if (note.Supersedes.Count > 0)
        {
            entries.Add(Pair("supersedes", Sequence(note.Supersedes)));
        }

        return new FrontMapping(entries);
    }

    private static bool TryParseVersion(string text, out long version) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1;

    private static bool TryParseBool(string text, out bool value)
    {
        value = text == "true";
        return text is "true" or "false";
    }

    private static KeyValuePair<string, FrontValue> Pair(string key, FrontValue value) => KeyValuePair.Create(key, value);

    private static FrontScalar Scalar(string text) => new(text);

    private static FrontSequence Sequence(IEnumerable<string> items) =>
        new FrontSequence(items.Select(i => (FrontValue)new FrontScalar(i)).ToList());

    private static Dictionary<string, FrontValue> Dict(IEnumerable<KeyValuePair<string, FrontValue>> entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>Collects the first error while reading, so the reader can run every field before failing.</summary>
    private sealed class Reader
    {
        public NoteParseError? Error { get; private set; }

        public string Detail { get; private set; } = string.Empty;

        public void Fail(NoteParseError error, string detail)
        {
            if (Error is null)
            {
                Error = error;
                Detail = detail;
            }
        }

        public string? Scalar(Dictionary<string, FrontValue> map, string key, string scope, bool required = true)
        {
            if (!map.TryGetValue(key, out var value))
            {
                if (required)
                {
                    Fail(NoteParseError.MissingField, $"{scope}{key} is required.");
                }

                return null;
            }

            if (value is FrontScalar { Text.Length: > 0 } scalar)
            {
                return scalar.Text;
            }

            Fail(NoteParseError.InvalidField, $"{scope}{key} must be a non-empty scalar.");
            return null;
        }

        public T Parse<T>(Dictionary<string, FrontValue> map, string key, string scope, TryParseWire<T> parse)
            where T : struct
        {
            var text = Scalar(map, key, scope);
            if (text is not null && parse(text, out var value))
            {
                return value;
            }

            if (text is not null)
            {
                Fail(NoteParseError.InvalidField, $"{scope}{key} has an unknown value.");
            }

            return default;
        }

        public List<string> Strings(Dictionary<string, FrontValue> map, string key, string scope)
        {
            if (!map.TryGetValue(key, out var value))
            {
                return [];
            }

            if (value is FrontSequence sequence && sequence.Items.All(i => i is FrontScalar { Text.Length: > 0 }))
            {
                return sequence.Items.Cast<FrontScalar>().Select(s => s.Text).ToList();
            }

            Fail(NoteParseError.InvalidField, $"{scope}{key} must be a sequence of non-empty scalars.");
            return [];
        }

        public Dictionary<string, FrontValue>? Mapping(Dictionary<string, FrontValue> map, string key, string scope, bool required = true)
        {
            if (!map.TryGetValue(key, out var value))
            {
                if (required)
                {
                    Fail(NoteParseError.MissingField, $"{scope}{key} is required.");
                }

                return null;
            }

            if (value is FrontMapping mapping)
            {
                return Dict(mapping.Entries);
            }

            Fail(NoteParseError.InvalidField, $"{scope}{key} must be a mapping.");
            return null;
        }

        public void RejectUnknown(Dictionary<string, FrontValue> map, string[] known, string scope)
        {
            foreach (var key in map.Keys)
            {
                if (Array.IndexOf(known, key) < 0)
                {
                    Fail(NoteParseError.UnknownMemoryField, $"{scope}{key} is not a known field.");
                }
            }
        }
    }
}
