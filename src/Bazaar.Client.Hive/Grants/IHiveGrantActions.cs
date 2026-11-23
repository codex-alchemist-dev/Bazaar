namespace Bazaar.Client.Hive.Grants;

/// <summary>
/// The owner's grant and revoke actions on the Hive pane (doc 25, "Modules and client panels"). The pane only requests a
/// change; the server decides authority and state, and the matrix changes only when the server's next model shows it.
/// A head supplies the implementation through <see cref="Bazaar.Client.Panes.Abstractions.PaneModuleContext.With{T}"/>. 
/// <see cref="WireHiveGrantActions"/> is the wire implementation; with none supplied the pane shows its buttons disabled.
/// </summary>
public interface IHiveGrantActions
{
    /// <summary>Asks the server to grant one (from, to, kind) pair. Throws when the server cannot be reached.</summary>
    Task<HiveGrantResult> GrantAsync(HiveGrantKey key, CancellationToken ct);

    /// <summary>Asks the server to revoke one (from, to, kind) pair. Throws when the server cannot be reached.</summary>
    Task<HiveGrantResult> RevokeAsync(HiveGrantKey key, CancellationToken ct);
}
