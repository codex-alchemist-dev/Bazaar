namespace Bazaar.Client.Views;

/// <summary>
/// The data a module hands a view (doc 41c, sections 4 and 11.2). It exposes each declared source as a typed value and
/// raises one change notice per source. A view never fetches data itself.
/// </summary>
public interface IViewData
{
    /// <summary>The current value of every declared source, keyed by source name. Scalars are numbers, strings or bools; lists are lists of records.</summary>
    IReadOnlyDictionary<string, object?> Snapshot();

    /// <summary>Raised with the source name when that source changed. It is the only trigger for re-evaluating bindings.</summary>
    event Action<string>? Changed;
}
