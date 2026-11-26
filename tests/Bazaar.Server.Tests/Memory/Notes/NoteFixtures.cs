using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Memory.Notes;

/// <summary>Shared note and write fixtures for the memory note tests.</summary>
internal static class NoteFixtures
{
    public const string NoteId = "01J9ZABCDEFGHJKMNPQRSTVWXY";

    public const string PrivateLessonPath = "agents/alpha/memory/lessons/retry-backoff.md";

    public static MemoryNote Note(
        MemoryKind kind = MemoryKind.Procedural,
        ContentTrust source = ContentTrust.Agent,
        MemoryTier trust = MemoryTier.AgentVerified) =>
        new(
            Id: NoteId,
            Type: MemoryNoteTypes.TypeFor(kind),
            Title: "Retry backoff",
            Project: "example",
            Kind: kind,
            Status: MemoryNoteStatus.Active,
            Version: 1,
            Trust: trust,
            SourceTrust: source,
            Tainted: false,
            Origin: new MemoryOrigin("gc-1", ["msg-1", "msg-2"]),
            DerivedFrom: [],
            Subjects: [],
            Deprecated: null,
            Supersedes: []);

    /// <summary>A note as text, written through the document serializer.</summary>
    public static string Text(MemoryNote? note = null, string body = "Back off exponentially after each failure.") =>
        NoteDocument.Create(note ?? Note(), body).Serialize();

    public static MemoryWriteInput Provenance(
        ContentTrust source = ContentTrust.Agent,
        TaintState session = TaintState.Clean,
        bool inherits = false,
        MemoryActor actor = MemoryActor.Agent) =>
        new(source, session, inherits, actor);

    public static MemoryWriteRequest Request(
        string path = PrivateLessonPath,
        MemoryWriteInput? provenance = null,
        string? actorAgent = "alpha",
        bool retrievalEligible = true) =>
        new(path, provenance ?? Provenance(), actorAgent, retrievalEligible);
}
