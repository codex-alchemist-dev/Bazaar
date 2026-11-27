namespace Bazaar.Core;

/// <summary>What sort of actor a <see cref="Principal"/> is.</summary>
public enum PrincipalKind
{
    /// <summary>An autonomous software agent with its own identity.</summary>
    Agent,

    /// <summary>A human user.</summary>
    User,

    /// <summary>A non-human actor such as a device or daemon. Never a <see cref="User"/>.</summary>
    Service,

    /// <summary>The host itself acting on its own behalf, for example during setup or a scheduled job.</summary>
    System,
}
