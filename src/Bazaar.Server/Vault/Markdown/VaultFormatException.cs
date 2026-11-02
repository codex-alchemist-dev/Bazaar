namespace Bazaar.Server.Vault.Markdown;

/// <summary>A Vault document or its front matter does not follow the format in doc 18.</summary>
public sealed class VaultFormatException(string message) : Exception(message);
