namespace Bazaar.ModulePermissions.Fs;

/// <summary>Limits one module's file access. The defaults are safe; a host may lower them or raise them with care.</summary>
/// <param name="MaxPathLength">Longest module path accepted, in characters.</param>
/// <param name="MaxSegments">Most segments a module path may have, the root name included.</param>
/// <param name="MaxFileBytes">Most bytes <see cref="ModuleFileSystem.ReadAllBytesAsync"/> returns or <see cref="ModuleFileSystem.WriteAllBytesAsync"/> writes.</param>
/// <param name="MaxEnumerateEntries">Most entries <see cref="ModuleFileSystem.Enumerate"/> returns. A larger directory is refused, not truncated.</param>
public sealed record ModuleFsOptions(
    int MaxPathLength = 1024,
    int MaxSegments = 32,
    long MaxFileBytes = 64L * 1024 * 1024,
    int MaxEnumerateEntries = 10_000);
