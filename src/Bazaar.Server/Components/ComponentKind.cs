namespace Bazaar.Server.Components;

/// <summary>What a Bazaar Component is for (docs/design/BAZAAR-COMPONENTS.md section 2).</summary>
public enum ComponentKind
{
    /// <summary>Part of the kernel. Always enabled.</summary>
    Kernel,

    /// <summary>A base-app function such as GC, Kanban, Vault or agents.</summary>
    Feature,

    /// <summary>An infrastructure role implementation, such as a service host or database host.</summary>
    Provider,

    /// <summary>A model gateway wire-family adapter, or a gateway source.</summary>
    Adapter,

    /// <summary>A client pane, rendered by a client head.</summary>
    ClientPane,
}
