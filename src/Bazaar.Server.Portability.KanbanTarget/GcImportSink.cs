using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Portability.KanbanTarget;

/// <summary>
/// Message I/O for the import GC. Appending and reading message text needs the GC write path that lands in PR #1246
/// (AppendResult.MessageId and the message text source). Until then the sink is built against this seam and tested with fakes.
/// </summary>
public interface IImportGcMessages
{
    /// <summary>Appends one message to the GC and returns its id.</summary>
    Task<Guid> AppendAsync(Guid gcId, string text, CancellationToken ct);

    /// <summary>The text of every message in the GC, in order.</summary>
    Task<IReadOnlyList<string>> ReadTextsAsync(Guid gcId, CancellationToken ct);
}

/// <summary>
/// The import GC sink (doc 44 section 8.2, PROPOSED import GC). Each job gets one GC of type "general", created through
/// <see cref="IGcProvisioner"/>. Its id is derived from the source and the job, so a retry finds the same GC, and the job id
/// is kept in metadata so <see cref="ReadJobMessagesAsync"/> can find it again.
/// </summary>
public sealed class GcImportSink(IGcProvisioner provisioner, IImportGcMessages messages, GcPrincipal owner, TimeProvider clock) : IImportGcSink
{
    /// <summary>The seeded GC type for import rooms.</summary>
    public const string TypeKey = "general";

    /// <summary>Metadata key that holds the job id.</summary>
    public const string JobMetadataKey = "import.job_id";

    /// <summary>Stable GC id for a job: the first 16 bytes of SHA-256 over the source and the job id.</summary>
    public static Guid JobGcId(string jobId, string sourceName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"import-gc|{sourceName}|{jobId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public async Task<Guid> EnsureJobGcAsync(string jobId, string sourceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        var gcId = JobGcId(jobId, sourceName);
        var title = $"{sourceName} import {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC";
        // A repeat call with the same id changes nothing and returns false, so there is no need to read its result.
        await provisioner.CreateAsync(gcId, TypeKey, title, owner, new Dictionary<string, string> { [JobMetadataKey] = jobId }, ct)
            .ConfigureAwait(false);
        return gcId;
    }

    public Task<Guid> AppendMessageAsync(Guid gcId, string text, CancellationToken ct) => messages.AppendAsync(gcId, text, ct);

    public async Task<IReadOnlyList<string>> ReadJobMessagesAsync(string jobId, CancellationToken ct)
    {
        var gcId = await provisioner.FindAsync(TypeKey, owner.Id, JobMetadataKey, jobId, ct).ConfigureAwait(false);
        return gcId is null ? [] : await messages.ReadTextsAsync(gcId.Value, ct).ConfigureAwait(false);
    }
}
