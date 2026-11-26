using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal;

/// <summary>Source of per-member grants on agent terminals. The default grants nothing (deny by default).</summary>
public interface ITerminalGrantSource
{
    /// <summary>May this human member view the terminal.</summary>
    bool IsViewGranted(string memberId, TerminalInfo terminal);

    /// <summary>May this human member write to the terminal. A write grant also allows viewing.</summary>
    bool IsWriteGranted(string memberId, TerminalInfo terminal);
}

/// <summary>Grants nothing.</summary>
public sealed class NoTerminalGrants : ITerminalGrantSource
{
    /// <inheritdoc />
    public bool IsViewGranted(string memberId, TerminalInfo terminal) => false;

    /// <inheritdoc />
    public bool IsWriteGranted(string memberId, TerminalInfo terminal) => false;
}

/// <summary>
/// The default policy (doc 52, section 4): the owner may view and write every terminal, an agent only its own, a human member
/// only what <see cref="ITerminalGrantSource"/> grants. Everything else is denied. An agent caller never counts as the owner.
/// </summary>
public sealed class DefaultTerminalAccessPolicy : ITerminalAccessPolicy
{
    private readonly ITerminalGrantSource _grants;

    /// <summary>Creates the policy over a grant source.</summary>
    public DefaultTerminalAccessPolicy(ITerminalGrantSource grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        _grants = grants;
    }

    /// <inheritdoc />
    public bool CanView(TerminalCaller caller, TerminalInfo terminal)
    {
        if (!Valid(caller, terminal))
        {
            return false;
        }

        if (caller.IsAgent)
        {
            return Owns(caller, terminal);
        }

        return caller.IsOwner || _grants.IsViewGranted(caller.MemberId, terminal) || _grants.IsWriteGranted(caller.MemberId, terminal);
    }

    /// <inheritdoc />
    public bool CanWrite(TerminalCaller caller, TerminalInfo terminal)
    {
        if (!Valid(caller, terminal))
        {
            return false;
        }

        if (caller.IsAgent)
        {
            return Owns(caller, terminal);
        }

        return caller.IsOwner || _grants.IsWriteGranted(caller.MemberId, terminal);
    }

    private static bool Valid(TerminalCaller? caller, TerminalInfo? terminal) =>
        caller is not null && terminal is not null && !string.IsNullOrWhiteSpace(caller.MemberId);

    private static bool Owns(TerminalCaller caller, TerminalInfo terminal) =>
        !string.IsNullOrEmpty(terminal.AgentId) && string.Equals(caller.MemberId, terminal.AgentId, StringComparison.Ordinal);
}
