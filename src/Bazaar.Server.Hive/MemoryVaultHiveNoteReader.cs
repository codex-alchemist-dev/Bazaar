using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Tools;

namespace Bazaar.Server.Hive;

/// <summary>
/// <see cref="IHiveNoteReader"/> over the memory Vault (<see cref="IMemoryVault"/>). The copy flow reads only Hive copies it wrote
/// itself (to reuse or deprecate them), so the reader serves <c>hive/**</c> paths and nothing else: a source note under a project
/// or an agent's private area is never returned, which keeps a read from crossing a project pair. It reads as the maintenance
/// principal and asks <see cref="IVaultAccess"/> first (docs/design/08-memory-system.md, section 8); a malformed path, a path
/// outside <c>hive/</c>, a refusal and a missing file all answer null.
/// </summary>
public sealed class MemoryVaultHiveNoteReader : IHiveNoteReader
{
    private const string _area = "hive/";

    private readonly IMemoryVault _vault;
    private readonly IVaultAccess _access;

    /// <summary>Creates the reader.</summary>
    public MemoryVaultHiveNoteReader(IMemoryVault vault, IVaultAccess access)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(access);
        _vault = vault;
        _access = access;
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        if (!path.StartsWith(_area, StringComparison.Ordinal) || !VaultPath.TryParse(path, out var vaultPath))
        {
            return null;
        }

        if (!_access.CanRead(VaultPrincipal.Maintenance(), vaultPath))
        {
            return null;
        }

        return await _vault.ReadNoteAsync(vaultPath, ct).ConfigureAwait(false);
    }
}
