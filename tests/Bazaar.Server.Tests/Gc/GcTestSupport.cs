using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Tests.Gc;

internal sealed class RecordingNotifier : IGcNotifier
{
    private readonly Lock _gate = new();
    private readonly List<(Guid Gc, long Seq)> _calls = [];

    public IReadOnlyList<(Guid Gc, long Seq)> Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public void Notify(Guid gcId, long seq)
    {
        lock (_gate)
            _calls.Add((gcId, seq));
    }
}

internal sealed class AllowAllAccess : IGcReadAccess
{
    public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) => ValueTask.FromResult(GcReadGrant.Full);
}

internal static class GcTestData
{
    public static readonly GcPrincipal Viewer = new(Guid.Parse("00000000-0000-7000-8000-0000000000aa"), PrincipalKind.Owner, "viewer");

    public static AppendRequest System(Guid gc, string key, Guid? author = null) =>
        new(gc, GcEventKinds.System, AuthorKind.System, author ?? Guid.Parse("00000000-0000-7000-8000-000000000001"), SourceTrust.System, key);
}
