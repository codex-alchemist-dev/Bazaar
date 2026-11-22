namespace Bazaar.Server.SealedBootstrap;

/// <summary>Operator settings of the sealed bootstrap module (doc 61 work package 5).</summary>
public sealed class SealedBootstrapOptions
{
    /// <summary>
    /// Plaintext files to import once, by value name (<c>Bazaar:SealedBootstrap:ImportFrom:&lt;name&gt;</c> = absolute path). Each is sealed,
    /// verified and then wiped before the first bootstrap read; a source that no longer exists is skipped. Empty by default. There is no
    /// setting to keep the plaintext after a verified import.
    /// </summary>
    public IReadOnlyDictionary<string, string> ImportFrom { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
