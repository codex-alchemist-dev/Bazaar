namespace Bazaar.Server.Setup;

/// <summary>
/// Finds a template version by id (doc 45 section 1.8). Lookups are exact: the id and the full semantic version must match, and
/// version ranges are never resolved. A catalog holds where versions come from; the template's own files come from its source.
/// </summary>
public interface ITemplateCatalog
{
    /// <summary>Returns the source for exactly <paramref name="templateId"/> at exactly <paramref name="version"/>, or null when the catalog does not hold it.</summary>
    Task<ITemplateSource?> FindAsync(string templateId, string version, CancellationToken cancellationToken = default);
}
