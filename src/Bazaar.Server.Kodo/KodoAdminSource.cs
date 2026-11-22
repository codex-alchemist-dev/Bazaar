using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The doc 51 Kodo port over the in-process read model (<see cref="IKodoStatusSource"/>, the same one behind
/// <c>/api/kodo/v1/status</c>). A switched-off module and a module before its first pass answer with a view that says so, not with
/// an error, so the panel shows the reason and not "unavailable".
/// </summary>
internal sealed class KodoAdminSource(IKodoStatusSource source, TimeProvider time) : IAdminKodoSource
{
    public Task<AdminKodoView> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AdminKodoView view = !source.Enabled
            ? KodoAdminViews.Disabled()
            : source.Latest is { } snapshot
                ? KodoAdminViews.FromSnapshot(snapshot, time.GetUtcNow(), KodoAdminViews.DefaultStaleAfter)
                : KodoAdminViews.NotReady();
        return Task.FromResult(view);
    }
}
