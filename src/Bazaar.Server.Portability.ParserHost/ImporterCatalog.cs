using System.Reflection;

namespace Bazaar.Server.Portability.ParserHost;

/// <summary>
/// The importers this child can run: the built-in ones in Bazaar.Server.Portability plus any importer assembly the
/// server named on the command line. Importers load here, in the child, never in the server process.
/// </summary>
internal sealed class ImporterCatalog
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public ImporterCatalog(IEnumerable<string> extraAssemblyPaths)
    {
        AddFrom(typeof(IPortabilityImporter).Assembly);
        foreach (var path in extraAssemblyPaths)
        {
            AddFrom(Assembly.LoadFrom(path));
        }
    }

    public IReadOnlyList<(string Id, string Version)> List(ImportLimits limits) =>
        [.. _types.Values.Select(t => Create(t, limits)).Select(i => (i.Id, i.Version)).OrderBy(i => i.Id, StringComparer.Ordinal)];

    public IPortabilityImporter? Find(string id, ImportLimits limits)
    {
        foreach (var type in _types.Values)
        {
            var importer = Create(type, limits);
            if (string.Equals(importer.Id, id, StringComparison.Ordinal)) return importer;
        }
        return null;
    }

    private static IPortabilityImporter Create(Type type, ImportLimits limits)
    {
        var withLimits = type.GetConstructor([typeof(ImportLimits)]);
        var instance = withLimits is not null
            ? withLimits.Invoke([limits])
            : Activator.CreateInstance(type);
        return (IPortabilityImporter)instance!;
    }

    private void AddFrom(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !type.IsPublic || !typeof(IPortabilityImporter).IsAssignableFrom(type)) continue;
            if (type.GetConstructor([typeof(ImportLimits)]) is null && type.GetConstructor(Type.EmptyTypes) is null) continue;
            _types[type.FullName ?? type.Name] = type;
        }
    }
}
