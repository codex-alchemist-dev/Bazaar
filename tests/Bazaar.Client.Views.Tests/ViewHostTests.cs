using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Views.Tests.Fixtures;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.Views.Tests;

public class ViewHostTests
{
    private static ViewDocument Pilot() =>
        ViewDocumentReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "pilots", "gc-feed.view.json")));

    private static readonly IReadOnlySet<string> _granted = new HashSet<string> { "gc.feed" };

    private sealed class FakeData : IViewData
    {
        public decimal Mentions { get; set; } = 1m;

        public event Action<string>? Changed;

        public void Raise(string source) => Changed?.Invoke(source);

        public IReadOnlyDictionary<string, object?> Snapshot() => ViewFixtures.Data(
            ("unreadGcs", 1m),
            ("mentions", Mentions),
            ("gcs", new List<IReadOnlyDictionary<string, object?>> { ViewFixtures.Data(("id", "g1"), ("name", "General"), ("unread", 1m), ("mentioned", true)) }));
    }

    private static ViewContext Context(Harness h) =>
        new(ViewFixtures.Theme(), h.Actions, new FakeLocalizer(), h.Errors.Add);

    private static string[] Texts(Control root) =>
        [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    [AvaloniaFact]
    public void A_valid_document_with_granted_capabilities_loads_and_renders()
    {
        var h = new Harness();
        using var host = new ViewHost(Pilot(), new FakeData(), Context(h), _granted);

        Assert.True(host.IsAccepted);
        Assert.Contains("General", Texts((Control)host.Content!));
    }

    [AvaloniaFact]
    public void A_capability_the_manifest_does_not_grant_refuses_the_whole_pane_and_audits_once()
    {
        var h = new Harness();
        var audits = new List<(string, string)>();

        using var host = new ViewHost(Pilot(), new FakeData(), Context(h), new HashSet<string>(), (n, r) => audits.Add((n, r)));

        Assert.False(host.IsAccepted);
        Assert.Equal(ViewMapper.PlaceholderName, ((Border)host.Content!).Name);
        Assert.Single(audits);
        Assert.Equal("bazaar.gc.feed", audits[0].Item1);
        Assert.Contains("capability_not_granted", audits[0].Item2);
    }

    [AvaloniaFact]
    public void An_invalid_document_is_refused_with_the_validator_message()
    {
        var h = new Harness();
        var bad = Pilot() with { DslVersion = 99 };
        var audits = new List<(string, string)>();

        using var host = new ViewHost(bad, new FakeData(), Context(h), _granted, (n, r) => audits.Add((n, r)));

        Assert.False(host.IsAccepted);
        Assert.Contains("view.dsl_version", audits.Single().Item2);
    }

    [AvaloniaFact]
    public void A_change_notice_for_a_declared_source_rebuilds_the_view()
    {
        var h = new Harness();
        var data = new FakeData { Mentions = 0m };
        using var host = new ViewHost(Pilot(), data, Context(h), _granted);
        object? before = host.Content;

        data.Mentions = 3m;
        data.Raise("mentions");

        Assert.NotSame(before, host.Content);
    }

    [AvaloniaFact]
    public void A_notice_for_an_undeclared_source_or_after_dispose_changes_nothing()
    {
        var h = new Harness();
        var data = new FakeData();
        var host = new ViewHost(Pilot(), data, Context(h), _granted);
        object? first = host.Content;

        data.Raise("somethingElse");
        Assert.Same(first, host.Content);

        host.Dispose();
        data.Raise("mentions");
        Assert.Same(first, host.Content);
    }

    [Fact]
    public void Embedded_lookup_fails_closed_for_an_unknown_name()
    {
        Assert.Throws<ViewDeclarationException>(() => ViewDocuments.ReadEmbedded(typeof(ViewHostTests).Assembly, "no.such.view"));
    }
}
