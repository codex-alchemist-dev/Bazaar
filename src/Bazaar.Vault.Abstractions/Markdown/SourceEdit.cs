namespace Bazaar.Server.Vault.Markdown;

/// <summary>Replaces <see cref="Length"/> characters at <see cref="Start"/> of a document's source with <see cref="Replacement"/>.</summary>
public sealed record SourceEdit(int Start, int Length, string Replacement);
