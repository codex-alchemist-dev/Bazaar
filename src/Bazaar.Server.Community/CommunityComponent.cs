namespace Bazaar.Server.Community;

/// <summary>
/// Registration point for the community component. Host composition is the only caller.
/// TODO(component): replace with the AddBazaarCommunity() extension once the component model (PR #125) merges.
/// Migrations use provider folder "community" (Data/Sql/community/NNNN_name.sql) until components/&lt;name&gt; exists.
/// </summary>
public static class CommunityComponent
{
    /// <summary>Migration provider folder and history key for this component's SQL.</summary>
    public const string MigrationProvider = "community";
}
