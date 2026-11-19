namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Who is looking at the graph (doc 19, "Visibility"). It has the same shape as the Vault's caller, so the graph applies the Vault
/// access rule of doc 18: the owner reads everything, an agent reads shared documents and its own private memory
/// (<see cref="AgentOwner"/>, "agent:&lt;id&gt;"), and any other member reads shared documents only.
/// </summary>
public sealed record GraphViewer(bool IsOwner, string? AgentOwner)
{
    public static GraphViewer Owner { get; } = new(IsOwner: true, AgentOwner: null);

    /// <summary>
    /// A key for everything this viewer may see: viewers with equal scopes get the same projection, so a cache may share
    /// one entry between them and must never share it with another scope.
    /// </summary>
    public string Scope => IsOwner ? "owner" : AgentOwner is null ? "shared" : "private:" + AgentOwner;
}
