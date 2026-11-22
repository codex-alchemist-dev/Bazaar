using Bazaar.Install.Trust;

namespace Bazaar.Server.ModuleHost;

/// <summary>
/// Where the module host finds the installed, signed package of a module. The package is what the start guard re-verifies on
/// every start (doc 48 section 1), so the source must return the stored package as it is, not a rebuilt one.
/// </summary>
public interface IModulePackageSource
{
    /// <summary>The installed package for <paramref name="moduleId"/>, or null when none is installed (a compiled-in component).</summary>
    ValueTask<ModulePackage?> FindAsync(string moduleId, CancellationToken ct = default);
}

/// <summary>The default source: no module has a package, so the host puts nothing in force and reports every module as not packaged.</summary>
public sealed class NoModulePackages : IModulePackageSource
{
    /// <inheritdoc />
    public ValueTask<ModulePackage?> FindAsync(string moduleId, CancellationToken ct = default) => ValueTask.FromResult<ModulePackage?>(null);
}
