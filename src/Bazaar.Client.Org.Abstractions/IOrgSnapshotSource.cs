namespace Bazaar.Client.Org;

/// <summary>Optional source of Org snapshots for a head that fetches the graph itself instead of receiving it as pane data.</summary>
public interface IOrgSnapshotSource
{
    Task<OrgSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
