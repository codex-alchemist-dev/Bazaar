// Required CodeQL Analyze check runs on this head.
namespace Bazaar.Server.Gc.Read;

/// <summary>
/// Records how far a principal has read in one GC (<c>read_state</c>, docs/design/01, catch-up). The unread count is the
/// head seq minus this position. The position only moves forward: a lower seq never moves it back.
/// </summary>
public interface IReadStateWriter
{
    /// <summary>
    /// Marks the GC read up to <paramref name="seq"/> for the principal. Returns the stored position after the call, which is
    /// larger than <paramref name="seq"/> when the principal had already read further, and then nothing changed.
    /// Throws <see cref="Bazaar.Server.Gc.GcNotFoundException"/> when the GC does not exist.
    /// </summary>
    Task<long> MarkReadAsync(Guid principalId, Guid gcId, long seq, CancellationToken ct);
}
