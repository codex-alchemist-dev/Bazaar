namespace Bazaar.Server.Souls.Import;

/// <summary>One file received in a bundle, held in memory.</summary>
/// <param name="Path">The bundle-relative path as received. Validated by the importer, never trusted.</param>
/// <param name="Content">The file bytes.</param>
public sealed record BundleFileContent(string Path, ReadOnlyMemory<byte> Content);
