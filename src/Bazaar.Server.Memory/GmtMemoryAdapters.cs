using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory;

/// <summary>Gives Genetic Memory Transfer the platform's secret scanner (docs/design/25-company-systems-knowledge.md work package 9).</summary>
public sealed class GmtSecretScanner : IGmtSecretScanner
{
    /// <inheritdoc />
    public bool ContainsSecret(string text) => SecretScanner.ContainsSecret(text);
}

/// <summary>
/// Writes an accepted Genetic Memory Transfer lesson as a note in the receiving agent's private memory, through the memory write
/// path. The note carries its origin, is never above T2 (a tainted child's lesson is T1), and is never promoted by this flow.
/// </summary>
public sealed partial class GmtLessonWriter : IGmtLessonWriter
{
    private readonly IMemoryBatchSink _sink;

    /// <summary>Creates the writer.</summary>
    public GmtLessonWriter(IMemoryBatchSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    [GeneratedRegex("^[A-Za-z0-9_./@+:-]+$")]
    private static partial Regex PlainScalar();

    /// <inheritdoc />
    public async Task<GmtWriteResult> WriteAsync(GmtLessonWrite write, string writerAgentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrEmpty(writerAgentId);

        var kind = write.Kind == GmtLessonKind.Failure ? MemoryKind.Failure : MemoryKind.Procedural;
        if (!MemoryAreas.CheckPath(write.Path, kind).IsValid)
        {
            return new GmtWriteResult(GmtWriteStatus.PathInvalid);
        }

        var content = Build(write, kind);
        if (content is null)
        {
            return new GmtWriteResult(GmtWriteStatus.NoteInvalid);
        }

        var item = new MemoryWriteItem(Guid.NewGuid(), write.Path, content, null, null, "gmt: reviewed lesson");
        var result = await _sink.CommitAsync(MemoryBatch.Create(writerAgentId, [item]), ct).ConfigureAwait(false);
        return result.Succeeded ? new GmtWriteResult(GmtWriteStatus.Written) : new GmtWriteResult(GmtWriteStatus.CommitFailed, result.FailureReason);
    }

    private static string? Build(GmtLessonWrite write, MemoryKind kind)
    {
        var tier = write.Tainted ? MemoryTier.UntrustedDerived : MemoryTier.AgentVerified;
        var idSource = Encoding.UTF8.GetBytes(write.CandidateId.ToString("N", CultureInfo.InvariantCulture));
        var note = new MemoryNote(
            Convert.ToHexString(SHA256.HashData(idSource))[..26],
            MemoryNoteTypes.TypeFor(kind), write.Title, null, kind, MemoryNoteStatus.Active, 1, tier,
            ContentTrust.Agent, write.Tainted, null, [], [], null, []);

        var extras = new List<(string Key, string Value)>
        {
            ("origin_kind", "agent"),
            ("origin_agent", write.ChildAgent),
            ("transfer", "gmt"),
        };
        if (write.TraceRef is not null)
        {
            extras.Add(("trace_ref", write.TraceRef));
        }

        if (extras.Any(e => !PlainScalar().IsMatch(e.Value)))
        {
            return null;
        }

        var head = NoteDocument.Create(note, string.Empty).Serialize();
        var close = head.LastIndexOf("---\n", StringComparison.Ordinal);
        var sb = new StringBuilder(head[..close]);
        foreach (var (key, value) in extras)
        {
            sb.Append(key).Append(": ").Append(value).Append('\n');
        }

        var text = sb.Append("---\n").Append(write.Body).ToString();
        return NoteDocument.Parse(text).Document is null ? null : text;
    }
}
