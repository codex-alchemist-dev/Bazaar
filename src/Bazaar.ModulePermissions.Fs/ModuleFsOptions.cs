namespace Bazaar.ModulePermissions.Fs;

/// <summary>Limits one module's file access. The defaults are safe; a host may lower them or raise them with care.</summary>
/// <param name="MaxPathLength">Longest module path accepted, in characters.</param>
/// <param name="MaxSegments">Most segments a module path may have, the root name included.</param>
/// <param name="MaxFileBytes">Most bytes <see cref="ModuleFileSystem.ReadAllBytesAsync"/> returns, and the longest a file written through <see cref="ModuleFileSystem.WriteAllBytesAsync"/>, <see cref="ModuleFileSystem.OpenWrite"/> or <see cref="ModuleFileSystem.OpenAppend"/> may become.</param>
/// <param name="MaxEnumerateEntries">Most entries <see cref="ModuleFileSystem.Enumerate"/> returns. A larger directory is refused, not truncated.</param>
/// <param name="MaxRootBytes">Most bytes a module may keep in one root, counted by the <see cref="ModuleFileSystem"/> instance; must be at least <paramref name="MaxFileBytes"/>.</param>
public sealed record ModuleFsOptions(
    int MaxPathLength = 1024,
    int MaxSegments = 32,
    long MaxFileBytes = 64L * 1024 * 1024,
    int MaxEnumerateEntries = 10_000,
    long MaxRootBytes = 1024L * 1024 * 1024);
