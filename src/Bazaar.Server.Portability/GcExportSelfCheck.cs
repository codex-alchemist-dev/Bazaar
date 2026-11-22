using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Portability;

/// <summary>The outcome of a self-check. Each problem is one plain sentence. An empty list passes.</summary>
/// <param name="Problems">What was found wrong, in the order it was found.</param>
public sealed record GcExportSelfCheckResult(IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}

/// <summary>
/// Re-reads a written <c>gc.jsonl.v1</c> export and checks it against <c>head.json</c> (doc 44 section 6.2). It checks each file's
/// size and SHA-256, every line's hash chain, gap-free ascending event seqs, the counts and head hashes, that every message or edit
/// event has its version row at the same seq, and that <c>messages.jsonl</c> lists the message events in order.
/// </summary>
/// <remarks>
/// It reads only through the store, streaming one line at a time, and never trusts what the exporter kept in memory. So a line
/// that was dropped, reordered or altered is found, whether the damage came during the write or after it. It opens the events
/// file several times, once per pass, rather than holding it.
/// </remarks>
public static class GcExportSelfCheck
{
    public static async Task<GcExportSelfCheckResult> VerifyAsync(IExportFileStore store, Guid gcId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var problems = new List<string>();

        var headPath = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.HeadFile);
        var eventsPath = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.EventsFile);
        var messagesPath = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.MessagesFile);
        var versionsPath = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.VersionsFile);

        var headBytes = await ReadHeadAsync(store, headPath, problems, ct);
        if (headBytes is null)
        {
            return new GcExportSelfCheckResult(problems);
        }

        var head = GcExportHead.TryParse(headBytes, out var headProblem);
        if (head is null)
        {
            problems.Add(headProblem ?? "head.json is malformed");
            return new GcExportSelfCheckResult(problems);
        }

        if (head.GcId != gcId)
        {
            problems.Add("head.json belongs to another GC");
        }

        foreach (var path in new[] { eventsPath, messagesPath, versionsPath })
        {
            await CheckFileBytesAsync(store, head, path, problems, ct);
        }

        await CheckEventsAsync(store, head, eventsPath, problems, ct);
        await CheckVersionsAsync(store, head, eventsPath, versionsPath, problems, ct);
        await CheckMessagesAsync(store, head, eventsPath, messagesPath, problems, ct);
        if (head.MentionCount is not null)
        {
            await CheckMentionsAsync(store, head, gcId, problems, ct);
        }

        return new GcExportSelfCheckResult(problems);
    }

    /// <summary>Checks the event chain: gap-free ascending seqs, the chain hashes, and the counts against head.json.</summary>
    private static async Task CheckEventsAsync(IExportFileStore store, GcExportHead head, string path, List<string> problems, CancellationToken ct)
    {
        await using var stream = await OpenAsync(store, path, problems, ct);
        if (stream is null)
        {
            return;
        }

        var chain = new ChainState();
        long expected = 1;
        long last = 0;
        long messages = 0;
        try
        {
            await foreach (var ev in ReadChainAsync(stream, path, chain, problems, MapEvent, ct))
            {
                if (ev.Seq != expected)
                {
                    problems.Add($"{path}: seq {ev.Seq} found where seq {expected} was expected; the log has a gap, a duplicate or a reorder.");
                }

                expected = ev.Seq + 1;
                last = ev.Seq;
                if (ev.Kind == GcEventKinds.Message)
                {
                    messages++;
                }
            }
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            problems.Add($"{path} has a malformed line: {ex.Message}");
            return;
        }

        if (chain.Count != head.EventCount)
        {
            problems.Add($"{path} has {chain.Count} events; head.json says {head.EventCount}.");
        }

        if (last != head.HeadSeq)
        {
            problems.Add($"{path} ends at seq {last}; head.json says the head is seq {head.HeadSeq}.");
        }

        if (chain.Prev != head.EventHeadHash)
        {
            problems.Add($"{path}: the last hash does not match head.json.");
        }

        if (messages != head.MessageCount)
        {
            problems.Add($"{path} has {messages} message events; head.json says {head.MessageCount}.");
        }
    }

    /// <summary>
    /// Checks that each version row sits at the seq of its event, names the same message and version, and that every message or
    /// edit event has one. Tombstones and bodies are checked by the mapping. The version chain and count are checked here too.
    /// </summary>
    private static async Task CheckVersionsAsync(
        IExportFileStore store, GcExportHead head, string eventsPath, string versionsPath, List<string> problems, CancellationToken ct)
    {
        await using var versionStream = await OpenAsync(store, versionsPath, problems, ct);
        await using var eventStream = await OpenAsync(store, eventsPath, problems, ct);
        if (versionStream is null || eventStream is null)
        {
            return;
        }

        // Chain problems were reported by the events pass, so this pass reads the events quietly.
        var scratch = new List<string>();
        var versionChain = new ChainState();
        try
        {
            await using var events = ReadChainAsync(eventStream, eventsPath, new ChainState(), scratch, MapEvent, ct).GetAsyncEnumerator(ct);
            await using var rows = ReadChainAsync(versionStream, versionsPath, versionChain, problems, MapVersion, ct).GetAsyncEnumerator(ct);

            var rowAvailable = await rows.MoveNextAsync();
            while (await events.MoveNextAsync())
            {
                var ev = events.Current;
                while (rowAvailable && rows.Current.Seq < ev.Seq)
                {
                    problems.Add($"{versionsPath}: the version of message {rows.Current.MessageId} at seq {rows.Current.Seq} matches no event.");
                    rowAvailable = await rows.MoveNextAsync();
                }

                if (rowAvailable && rows.Current.Seq == ev.Seq)
                {
                    var row = rows.Current;
                    if (row.MessageId != ev.MessageId || row.Version != ev.Version)
                    {
                        problems.Add($"seq {ev.Seq}: the version row names message {row.MessageId} version {row.Version}, but the event names {ev.MessageId} version {ev.Version}.");
                    }

                    rowAvailable = await rows.MoveNextAsync();
                }
                else if (ev.Kind is GcEventKinds.Message or GcEventKinds.MessageEdit)
                {
                    problems.Add($"seq {ev.Seq}: a {ev.Kind} event has no version row.");
                }
            }

            while (rowAvailable)
            {
                problems.Add($"{versionsPath}: the version of message {rows.Current.MessageId} at seq {rows.Current.Seq} matches no event.");
                rowAvailable = await rows.MoveNextAsync();
            }
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            problems.Add($"{versionsPath} has a malformed line: {ex.Message}");
            return;
        }

        if (versionChain.Count != head.VersionCount)
        {
            problems.Add($"{versionsPath} has {versionChain.Count} versions; head.json says {head.VersionCount}.");
        }

        if (versionChain.Prev != head.VersionHeadHash)
        {
            problems.Add($"{versionsPath}: the last hash does not match head.json.");
        }
    }

    /// <summary>Checks that <c>messages.jsonl</c> lists the message events in order, with their first seqs, and that its counts match.</summary>
    private static async Task CheckMessagesAsync(
        IExportFileStore store, GcExportHead head, string eventsPath, string messagesPath, List<string> problems, CancellationToken ct)
    {
        await using var messageStream = await OpenAsync(store, messagesPath, problems, ct);
        await using var eventStream = await OpenAsync(store, eventsPath, problems, ct);
        if (messageStream is null || eventStream is null)
        {
            return;
        }

        var scratch = new List<string>();
        var messageChain = new ChainState();
        try
        {
            await using var events = ReadChainAsync(eventStream, eventsPath, new ChainState(), scratch, MapEvent, ct).GetAsyncEnumerator(ct);
            await using var lines = ReadChainAsync(messageStream, messagesPath, messageChain, problems, MapMessage, ct).GetAsyncEnumerator(ct);

            var lineAvailable = await lines.MoveNextAsync();
            while (await events.MoveNextAsync())
            {
                var ev = events.Current;
                if (ev.Kind != GcEventKinds.Message)
                {
                    continue;
                }

                if (!lineAvailable)
                {
                    problems.Add($"seq {ev.Seq}: the message event has no line in {messagesPath}.");
                    continue;
                }

                if (lines.Current.MessageId != ev.MessageId || lines.Current.FirstSeq != ev.Seq)
                {
                    problems.Add($"{messagesPath}: the line for the message at seq {ev.Seq} does not match the event.");
                }

                lineAvailable = await lines.MoveNextAsync();
            }

            while (lineAvailable)
            {
                problems.Add($"{messagesPath}: a line for message {lines.Current.MessageId} matches no message event.");
                lineAvailable = await lines.MoveNextAsync();
            }
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            problems.Add($"{messagesPath} has a malformed line: {ex.Message}");
            return;
        }

        if (messageChain.Count != head.MessageCount)
        {
            problems.Add($"{messagesPath} has {messageChain.Count} messages; head.json says {head.MessageCount}.");
        }

        if (messageChain.Prev != head.MessageHeadHash)
        {
            problems.Add($"{messagesPath}: the last hash does not match head.json.");
        }
    }

    /// <summary>Checks <c>mentions.jsonl</c> when head.json declares it: file bytes, the chain, the count and the head hash.</summary>
    private static async Task CheckMentionsAsync(IExportFileStore store, GcExportHead head, Guid gcId, List<string> problems, CancellationToken ct)
    {
        var path = GcJsonlFormat.PathFor(gcId, GcMentionsBundleSource.MentionsFile);
        await CheckFileBytesAsync(store, head, path, problems, ct);
        await using var stream = await OpenAsync(store, path, problems, ct);
        if (stream is null)
        {
            return;
        }

        var chain = new ChainState();
        try
        {
            await foreach (var seq in ReadChainAsync(
                stream, path, chain, problems, m => m.GetProperty("seq").GetInt64(), ct))
            {
                if (seq < 1 || seq > head.HeadSeq)
                {
                    problems.Add($"{path}: a mention at seq {seq} lies outside the log (head seq {head.HeadSeq}).");
                }
            }
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            problems.Add($"{path} has a malformed line: {ex.Message}");
            return;
        }

        if (chain.Count != head.MentionCount)
        {
            problems.Add($"{path} has {chain.Count} mentions; head.json says {head.MentionCount}.");
        }

        if (chain.Prev != (head.MentionHeadHash ?? GcJsonlFormat.GenesisHash))
        {
            problems.Add($"{path}: the last hash does not match head.json.");
        }
    }

    /// <summary>Checks each file's size and SHA-256 against head.json. A file head.json does not list is a problem.</summary>
    private static async Task CheckFileBytesAsync(IExportFileStore store, GcExportHead head, string path, List<string> problems, CancellationToken ct)
    {
        var expected = head.Files.FirstOrDefault(f => f.Path == path);
        if (expected is null)
        {
            problems.Add("head.json does not list " + path + ".");
            return;
        }

        await using var stream = await OpenAsync(store, path, problems, ct);
        if (stream is null)
        {
            return;
        }

        var actual = await GcJsonlFormat.HashAsync(path, stream, ct);
        if (actual.Bytes != expected.Bytes)
        {
            problems.Add($"{path} is {actual.Bytes} bytes; head.json says {expected.Bytes}.");
        }

        if (actual.Sha256 != expected.Sha256)
        {
            problems.Add($"{path}: sha256 does not match head.json.");
        }
    }

    /// <summary>Reads and chain-checks one JSON Lines file, yielding each line as <paramref name="map"/> reads it.</summary>
    private static async IAsyncEnumerable<T> ReadChainAsync<T>(
        Stream stream, string label, ChainState state, List<string> problems, Func<JsonElement, T> map,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            state.Count++;
            var problem = GcJsonlFormat.CheckLine(line, state.Prev, out var document, out var stored);
            if (problem is not null)
            {
                problems.Add($"{label} line {state.Count}: {problem}.");
            }

            // Carry the stored hash forward even when this line is wrong, so one bad line does not hide the lines after it.
            if (stored.Length > 0)
            {
                state.Prev = stored;
            }

            using (document)
            {
                if (document is not null)
                {
                    yield return map(document.RootElement);
                }
            }
        }
    }

    private static EventKey MapEvent(JsonElement e) => new(
        e.GetProperty("seq").GetInt64(),
        e.GetProperty("kind").GetString() ?? throw new FormatException("kind is null"),
        OptionalGuid(e, "messageId"),
        OptionalInt(e, "version"));

    private static VersionKey MapVersion(JsonElement v)
    {
        var tombstone = v.GetProperty("tombstone").GetBoolean();
        var body = v.GetProperty("body");
        if (tombstone != (body.ValueKind == JsonValueKind.Null))
        {
            throw new FormatException("tombstone and body disagree");
        }

        if (!tombstone)
        {
            _ = v.GetProperty("body").GetProperty("keyId").GetGuid();
            _ = Base64(body, "nonce");
            _ = Base64(body, "ciphertext");
        }

        return new VersionKey(
            v.GetProperty("seq").GetInt64(),
            v.GetProperty("messageId").GetGuid(),
            v.GetProperty("version").GetInt32());
    }

    private static MessageKey MapMessage(JsonElement m) => new(
        m.GetProperty("messageId").GetGuid(),
        m.GetProperty("firstSeq").GetInt64());

    private static Guid? OptionalGuid(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetGuid();

    private static int? OptionalInt(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt32();

    private static byte[] Base64(JsonElement body, string name) =>
        Convert.FromBase64String(body.GetProperty(name).GetString() ?? throw new FormatException(name + " is null"));

    private static bool IsMalformed(Exception ex) =>
        ex is FormatException or InvalidOperationException or KeyNotFoundException or JsonException or InvalidDataException;

    private static async Task<byte[]?> ReadHeadAsync(IExportFileStore store, string path, List<string> problems, CancellationToken ct)
    {
        await using var stream = await OpenAsync(store, path, problems, ct);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    /// <summary>Opens a file for reading. A missing file is a problem, not an exception.</summary>
    private static async Task<Stream?> OpenAsync(IExportFileStore store, string path, List<string> problems, CancellationToken ct)
    {
        try
        {
            return await store.OpenReadAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            problems.Add(path + " is missing.");
            return null;
        }
    }

    /// <summary>A chain's running state while its lines are read: the last hash and the number of lines seen.</summary>
    private sealed class ChainState
    {
        public string Prev { get; set; } = GcJsonlFormat.GenesisHash;

        public long Count { get; set; }
    }

    private sealed record EventKey(long Seq, string Kind, Guid? MessageId, int? Version);

    private sealed record VersionKey(long Seq, Guid MessageId, int Version);

    private sealed record MessageKey(Guid MessageId, long FirstSeq);
}
