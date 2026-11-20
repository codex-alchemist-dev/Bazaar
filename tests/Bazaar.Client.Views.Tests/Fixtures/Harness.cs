using Avalonia.Controls;
using Bazaar.Client.Theme;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests.Fixtures;

/// <summary>A mapper wired to a fake head. Errors collects every failure from a click or an input change.</summary>
internal sealed class Harness
{
    public RecordingActions Actions { get; } = new();

    public List<Exception> Errors { get; } = [];

    public Control Build(ViewDocument document, ThemeDefinition? theme = null, IReadOnlyDictionary<string, object?>? data = null) =>
        Mapper(theme).Build(document, data ?? DefaultData());

    public ViewMapper Mapper(ThemeDefinition? theme = null) =>
        new(new ViewContext(theme ?? ViewFixtures.Theme(), Actions, new FakeLocalizer(), Errors.Add));

    /// <summary>Two items for the list fixture, keyed by id.</summary>
    public static Dictionary<string, object?> DefaultData() => ViewFixtures.Data(
        ("items", new List<IReadOnlyDictionary<string, object?>>
        {
            ViewFixtures.Data(("id", "a"), ("title", "First")),
            ViewFixtures.Data(("id", "b"), ("title", "Second")),
        }));
}
