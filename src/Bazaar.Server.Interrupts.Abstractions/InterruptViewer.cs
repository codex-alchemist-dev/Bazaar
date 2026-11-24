namespace Bazaar.Server.Interrupts;

/// <summary>Who is reading the interrupt stacks (doc 24 section 5). The host derives it from the request's bearer.</summary>
/// <param name="Id">The viewer's principal id. An agent viewer's id is its agent id.</param>
/// <param name="IsCompanyOwner">True when the viewer is the company owner.</param>
public sealed record InterruptViewer(Guid Id, bool IsCompanyOwner)
{
    /// <summary>The least-privileged viewer: no identity, not the owner. Sees no reasons.</summary>
    public static InterruptViewer Anonymous { get; } = new(Guid.Empty, false);
}
