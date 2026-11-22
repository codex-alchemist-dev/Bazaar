namespace Bazaar.Server.Vault.Writes;

/// <summary>The write path into a Vault (doc 18, "Write path"). <c>VaultWriter</c> in Bazaar.Server.Vault is the implementation.</summary>
public interface IVaultWriter
{
    /// <summary>Records one agent edit on main, one at a time per Vault (doc 18, Write path).</summary>
    Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct);

    /// <summary>Creates a decision record with the next free number and commits it (doc 18, Write path).</summary>
    Task<VaultEditResult> CreateDecisionAsync(DecisionDraft draft, CancellationToken ct);
}
