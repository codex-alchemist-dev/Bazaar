using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>Compares the models stored for one provider with the ids the provider lists now. Pure: no I/O.</summary>
public static class CatalogueDiffer
{
    /// <summary>Ids compare ordinally (provider model ids are case-sensitive). Duplicate listed ids count once.</summary>
    public static CatalogueDiff Diff(IReadOnlyList<GwModel> known, IReadOnlyList<string> listed)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(listed);

        var listedSet = new HashSet<string>(listed.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        var added = new List<string>();
        var missing = new List<string>();
        var returned = new List<string>();
        var unchanged = new List<string>();

        var knownIds = new HashSet<string>(known.Select(m => m.ModelId), StringComparer.Ordinal);
        foreach (var id in listedSet.Order(StringComparer.Ordinal))
        {
            if (!knownIds.Contains(id))
            {
                added.Add(id);
            }
        }

        foreach (var model in known.OrderBy(m => m.ModelId, StringComparer.Ordinal))
        {
            var isListed = listedSet.Contains(model.ModelId);
            var isMissing = string.Equals(model.Status, GwModel.StatusMissing, StringComparison.Ordinal);
            if (isListed && isMissing)
            {
                returned.Add(model.ModelId);
            }
            else if (isListed)
            {
                unchanged.Add(model.ModelId);
            }
            else if (!isMissing)
            {
                missing.Add(model.ModelId);
            }
        }

        return new CatalogueDiff(added, missing, returned, unchanged);
    }
}
