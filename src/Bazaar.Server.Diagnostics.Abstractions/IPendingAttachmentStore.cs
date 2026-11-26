using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Server.Diagnostics;

/// <summary>
/// A stored report that names a screenshot still uploading. The report is kept; the screenshot attaches when it finishes (doc 43
/// section 5.2).
/// </summary>
/// <param name="AttachmentId">The attachment the report names.</param>
/// <param name="ReportId">The stored report.</param>
/// <param name="InstallId">The install the report belongs to, which keys the screenshot's seal.</param>
public sealed record PendingReport(Guid AttachmentId, Guid ReportId, Guid InstallId);

/// <summary>
/// Holds one half of a report and its screenshot until the other half arrives, so the two may come in either order. A held entry
/// is only handed back to the device that sent it, and it expires. Nothing here is durable: an entry lost on restart is a
/// screenshot the report is kept without, which doc 43 allows.
/// </summary>
public interface IPendingAttachmentStore
{
    /// <summary>Holds a finished upload until its report is stored. Returns false when the store is full or the id is already held.</summary>
    bool HoldUpload(string deviceId, IngestionAttachment upload);

    /// <summary>Takes the held upload with this attachment id, when it was uploaded from <paramref name="deviceId"/>.</summary>
    bool TryTakeUpload(string deviceId, Guid attachmentId, out IngestionAttachment? upload);

    /// <summary>Holds a stored report until its screenshot finishes. Returns false when the store is full or the id is already held.</summary>
    bool HoldReport(string deviceId, PendingReport report);

    /// <summary>Takes the waiting report for this attachment id, when it was submitted from <paramref name="deviceId"/>.</summary>
    bool TryTakeReport(string deviceId, Guid attachmentId, out PendingReport? report);
}
