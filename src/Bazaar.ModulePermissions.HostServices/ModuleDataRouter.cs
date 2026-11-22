using System.Collections.Frozen;
using Bazaar.Server.ModuleHost;

namespace Bazaar.ModulePermissions.HostServices;

/// <summary>
/// The unguarded <see cref="IModuleData"/> the guard stands in front of: it sends a request to the one
/// <see cref="IModuleDataSource"/> whose component name equals the request's, compared exactly. It checks no permission.
/// </summary>
public sealed class ModuleDataRouter : IModuleData
{
    private readonly FrozenDictionary<string, IModuleDataSource> _sources;

    /// <summary>Builds the router. Two sources for one component, or a component name that is not a single clean segment, are refused.</summary>
    public ModuleDataRouter(IEnumerable<IModuleDataSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var map = new Dictionary<string, IModuleDataSource>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source, nameof(sources));
            var component = source.Component;
            if (!ModuleTargetRules.IsValidTarget(component) || component.Contains('/', StringComparison.Ordinal))
            {
                throw new ArgumentException("A data source has a component name that no permission can name.", nameof(sources));
            }

            if (!map.TryAdd(component, source))
            {
                throw new ArgumentException($"Two data sources claim the component '{component}'.", nameof(sources));
            }
        }

        _sources = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<ModuleDataPage> ReadAsync(ModuleDataReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Source(request.Component).ReadAsync(request, ct);
    }

    /// <inheritdoc />
    public ValueTask<ModuleDataWriteResult> WriteAsync(ModuleDataWriteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = Source(request.Component);
        if (!source.SupportsWrite)
        {
            throw new ModuleDataUnavailableException("The component does not take writes from modules.");
        }

        return source.WriteAsync(request, ct);
    }

    private IModuleDataSource Source(string? component) =>
        component is not null && _sources.TryGetValue(component, out var source)
            ? source
            : throw new ModuleDataUnavailableException("The component offers no data to modules.");
}

/// <summary>A data request named a component that offers nothing, or a write the component does not take.</summary>
public sealed class ModuleDataUnavailableException(string message) : InvalidOperationException(message);
