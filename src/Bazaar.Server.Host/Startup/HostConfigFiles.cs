using Bazaar.Server.Hosting;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Adds the optional host config files that live under the data root, which is only known after the first configuration
/// sources are read. Currently one file: <c>config/kek.json</c>, written by the setup apply step (doc 45) with the
/// <c>Bazaar:Identity:KekProvider</c> choice. The file sits below environment variables, so an operator override still wins.
/// </summary>
public static class HostConfigFiles
{
    /// <summary>Path of the KEK settings file relative to the data root.</summary>
    public const string KekFileRelativePath = "config/kek.json";

    /// <summary>The KEK settings file for <paramref name="dataRoot"/>.</summary>
    public static string KekFilePath(string dataRoot) => Path.Combine(dataRoot, "config", "kek.json");

    /// <summary>
    /// Reads <c>Bazaar:Host:DataRoot</c> from what the builder already holds and inserts the KEK settings file before the
    /// first environment-variable source (or last when there is none). Does nothing when the data root is blank or relative.
    /// Idempotent: a second call adds no second source.
    /// </summary>
    public static IConfigurationBuilder AddKekHostConfig(this IConfigurationBuilder builder)
    {
        var dataRoot = builder.Build()[$"{BazaarHostOptions.SectionName}:DataRoot"];
        if (string.IsNullOrWhiteSpace(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
        {
            return builder;
        }

        var path = KekFilePath(dataRoot);
        if (builder.Sources.OfType<JsonConfigurationSource>().Any(s => IsFile(s, path)))
        {
            return builder;
        }

        var source = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();

        var at = builder.Sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource);
        if (at < 0)
        {
            builder.Sources.Add(source);
        }
        else
        {
            builder.Sources.Insert(at, source);
        }

        return builder;
    }

    // Building a source splits an absolute path into a provider root and a file name, so compare the joined form.
    private static bool IsFile(JsonConfigurationSource source, string path)
    {
        var joined = source.FileProvider is PhysicalFileProvider physical && source.Path is not null
            ? Path.Combine(physical.Root, source.Path)
            : source.Path;
        return string.Equals(joined, path, StringComparison.Ordinal);
    }
}
