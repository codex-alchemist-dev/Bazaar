namespace Bazaar.Server.Vault.Writes;

/// <summary>The write path into a Vault (doc 18, "Write path"). <see cref="VaultWriter"/> is the implementation.</summary>
public interface IVaultWriter
{
    /// <summary>Records one agent edit on main, one at a time per Vault (doc 18, Write path).</summary>
    Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct);
}
