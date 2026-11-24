namespace Bazaar.Server.DevSandbox.Wasm;

/// <summary>What an import is: the action kind it reports and the permission class it needs.</summary>
/// <param name="Kind">The action kind reported to the runner.</param>
/// <param name="Class">The permission class (doc 48 section 1 vocabulary).</param>
public sealed record WasmImportInfo(SandboxActionKind Kind, string Class);

/// <summary>
/// The fixed table of host imports a module may ever be linked to. Anything not in this table is never linked, whatever the grants say.
/// </summary>
public static class WasmImportCatalogue
{
    /// <summary>Class reported for a declared import that is not in the catalogue.</summary>
    public const string UnknownImportClass = "wasm.import.unknown";

    private static readonly Dictionary<string, WasmImportInfo> _table = new(StringComparer.Ordinal)
    {
        ["bazaar.vault_read"] = new(SandboxActionKind.HostCall, "vault.read"),
        ["bazaar.http_fetch"] = new(SandboxActionKind.NetworkAttempt, "net.egress"),
        ["bazaar.file_read"] = new(SandboxActionKind.FileAccess, "fs.read"),
        ["bazaar.secret_read"] = new(SandboxActionKind.SecretRead, "secret.read"),
    };

    /// <summary>Looks an import up by its catalogue key (<c>module.name</c>, case-sensitive).</summary>
    /// <param name="key">The catalogue key.</param>
    /// <param name="info">The entry, when found.</param>
    /// <returns>True when the import is in the catalogue.</returns>
    public static bool TryGet(string key, out WasmImportInfo info)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _table.TryGetValue(key, out info!);
    }
}
