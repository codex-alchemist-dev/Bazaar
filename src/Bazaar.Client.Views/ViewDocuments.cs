using System.Reflection;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views;

/// <summary>Reads a module's embedded view documents (doc 41c, section 11.1): <c>Views/&lt;name&gt;.view.json</c>.</summary>
public static class ViewDocuments
{
    /// <summary>
    /// Reads the embedded document <paramref name="name"/> from <paramref name="assembly"/>. The file name must match the
    /// document's own <c>name</c> field, so a renamed file cannot hide a stale document.
    /// </summary>
    public static ViewDocument ReadEmbedded(Assembly assembly, string name)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string suffix = $".Views.{name}.view.json";
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (resource is null)
        {
            throw new ViewDeclarationException($"No embedded view document '{name}' in {assembly.GetName().Name}.");
        }

        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        ViewDocument document = ViewDocumentReader.Read(reader.ReadToEnd());
        if (!string.Equals(document.Name, name, StringComparison.Ordinal))
        {
            throw new ViewDeclarationException($"Embedded view file '{name}' declares the name '{document.Name}'.");
        }

        return document;
    }
}
