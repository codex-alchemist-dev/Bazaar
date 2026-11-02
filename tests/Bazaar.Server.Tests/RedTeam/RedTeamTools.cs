using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Tests.RedTeam;

/// <summary>
/// The representative tools the replay runs against: one Tier 0 read, one Tier 1 post, one Tier 2 push, one Tier 2
/// package install and one Tier 3 human-performed action. Names and tiers are test fixtures, not production tools.
/// </summary>
public static class RedTeamTools
{
    /// <summary>Tier 0 read. Allowed in every session role that may read.</summary>
    public const string VaultRead = "vault.read";

    /// <summary>Tier 1 post. Allowed with a notice in clean and soft sessions, Tier 2 under hard taint.</summary>
    public const string GcPost = "gc.post";

    /// <summary>Tier 2 push. Needs an owner approval unless the session says otherwise.</summary>
    public const string RepoPush = "repo.push";

    /// <summary>Tier 2 package install.</summary>
    public const string PackageInstall = "package.install";

    /// <summary>Tier 3. Only a human may perform it.</summary>
    public const string AccountCreate = "account.create";

    /// <summary>The registry the replay uses. Built through the production <see cref="CapabilityRegistry"/> so floors are checked.</summary>
    public static CapabilityRegistry Registry { get; } = CapabilityRegistry.Create(
    [
        new ToolCapability(VaultRead, ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader | SessionRoles.Actor),
        new ToolCapability(GcPost, ToolTier.AutonomousWithNotice, CapabilityClass.Post, SessionRoles.Actor),
        new ToolCapability(RepoPush, ToolTier.OwnerApproves, CapabilityClass.Push, SessionRoles.Actor),
        new ToolCapability(PackageInstall, ToolTier.OwnerApproves, CapabilityClass.InstallPackage, SessionRoles.Actor),
        new ToolCapability(AccountCreate, ToolTier.HumanPerforms, CapabilityClass.CreateAccount, SessionRoles.Actor),
    ]);
}
