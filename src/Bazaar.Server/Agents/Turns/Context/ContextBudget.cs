namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>
/// Per-section token ceilings (doc 06 "Settings": <c>runtime.budget.*</c>). The defaults are the documented values.
/// Inbox has no ceiling; it is always included verbatim.
/// </summary>
public sealed record ContextBudget(int SystemTokens, int MemoryTokens, int TaskTokens, int VaultTokens, int BackreadTokens)
{
    /// <summary>The documented defaults: system 4000, memory 4000, task 2000, vault 4000, backread 12000.</summary>
    public static ContextBudget Defaults { get; } = new(4000, 4000, 2000, 4000, 12000);

    /// <summary>Throws when any ceiling is negative. The assembler calls this before it uses a budget.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A ceiling is negative; the parameter name is the property name.</exception>
    public void EnsureValid()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(SystemTokens, nameof(SystemTokens));
        ArgumentOutOfRangeException.ThrowIfNegative(MemoryTokens, nameof(MemoryTokens));
        ArgumentOutOfRangeException.ThrowIfNegative(TaskTokens, nameof(TaskTokens));
        ArgumentOutOfRangeException.ThrowIfNegative(VaultTokens, nameof(VaultTokens));
        ArgumentOutOfRangeException.ThrowIfNegative(BackreadTokens, nameof(BackreadTokens));
    }

    /// <summary>The token ceiling of a budgeted section.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="section"/> is <see cref="ContextSection.Inbox"/>, which has no ceiling.</exception>
    public int For(ContextSection section) => section switch
    {
        ContextSection.System => SystemTokens,
        ContextSection.Memory => MemoryTokens,
        ContextSection.TaskState => TaskTokens,
        ContextSection.Vault => VaultTokens,
        ContextSection.GcBackread => BackreadTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "The inbox section has no token ceiling."),
    };
}
