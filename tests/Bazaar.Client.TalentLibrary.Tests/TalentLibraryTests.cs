using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.TalentLibrary.Avalonia;
using Bazaar.Client.TalentLibrary.Http;

namespace Bazaar.Client.TalentLibrary.Tests;

public sealed class TalentLibraryTests
{
    private static readonly LibraryEntry _entry = new("reviewer", "Reviewer", 2, "quarantined", 1, 2);
    private static readonly LibraryVersion _v1 = new(1, "a", "x", DateTimeOffset.UnixEpoch, "released");
    private static readonly LibraryVersion _v2 = new(2, "b", "x", DateTimeOffset.UnixEpoch, "quarantined");

    private static LibraryDiff Diff(string[]? toolsAdded = null) => new(
        "reviewer", 1, 2, null, null,
        [new LibraryLine("same", "keep"), new LibraryLine("removed", "old"), new LibraryLine("added", "new")],
        toolsAdded ?? [], [], [], [], [], [], toolsAdded is { Length: > 0 });

    [AvaloniaFact]
    public async Task Selecting_a_template_with_two_versions_shows_the_latest_diff()
    {
        var vm = new TalentLibraryViewModel(new Source { Diffs = { [(1, 2)] = Diff(["shell"]) } });
        await vm.RefreshAsync();
        await vm.SelectAsync(vm.Entries[0]);

        Assert.Equal("Version 1 to version 2", vm.DiffTitle);
        Assert.Equal(["  keep", "- old", "+ new"], vm.Lines.Select(l => l.Mark + l.Text).ToArray());
        Assert.Contains(vm.Summary, s => s.StartsWith("Tools added: shell", StringComparison.Ordinal));
        Assert.Contains(vm.Summary, s => s.Contains("fresh tool approval", StringComparison.Ordinal));
        Assert.Equal(["Version 1: released", "Version 2: quarantined"], vm.Versions.Select(v => v.Label).ToArray());
    }

    [AvaloniaFact]
    public async Task Tapping_two_versions_compares_them_oldest_first_whatever_the_tap_order()
    {
        var source = new Source { Diffs = { [(1, 2)] = Diff() } };
        var vm = new TalentLibraryViewModel(source);
        await vm.RefreshAsync();
        await vm.SelectAsync(vm.Entries[0]);
        source.Requested.Clear();

        await vm.PickVersionAsync(_v2);
        Assert.True(vm.HasNotice);
        await vm.PickVersionAsync(_v1);

        Assert.Equal([(1, 2)], source.Requested);
        Assert.False(vm.HasNotice);
    }

    [AvaloniaFact]
    public async Task A_single_version_template_shows_no_diff_and_identical_versions_say_so()
    {
        var one = new Source { Entries = [_entry with { LatestVersion = 1, VersionCount = 1 }], Versions = [_v1] };
        var vm = new TalentLibraryViewModel(one);
        await vm.RefreshAsync();
        await vm.SelectAsync(vm.Entries[0]);
        Assert.False(vm.HasDiff);

        var same = new TalentLibraryViewModel(new Source { Diffs = { [(1, 2)] = new LibraryDiff("reviewer", 1, 2, null, null, [new LibraryLine("same", "x")], [], [], [], [], [], [], false) } });
        await same.RefreshAsync();
        await same.SelectAsync(same.Entries[0]);
        Assert.Equal(["These versions are the same."], same.Summary);
    }

    [AvaloniaFact]
    public async Task An_unreachable_server_shows_a_notice_and_keeps_the_view_working()
    {
        var vm = new TalentLibraryViewModel(new Source { Throws = true });
        await vm.RefreshAsync();
        Assert.Equal("Could not reach the server.", vm.Notice);
        Assert.Empty(vm.Entries);
    }

    [AvaloniaFact]
    public async Task The_view_renders_the_entries_and_the_diff()
    {
        var vm = new TalentLibraryViewModel(new Source { Diffs = { [(1, 2)] = Diff(["shell"]) } });
        await vm.RefreshAsync();
        await vm.SelectAsync(vm.Entries[0]);
        var view = new TalentLibraryView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        Assert.Equal("Version 1 to version 2", view.FindControl<TextBlock>("DiffTitleText")!.Text);
        Assert.Single(view.FindControl<ItemsControl>("EntryList")!.Items);
        Assert.Equal(3, view.FindControl<ItemsControl>("LineList")!.Items.Count);
    }

    [Fact]
    public async Task The_http_client_reads_the_three_routes_with_the_bearer_token_and_maps_404()
    {
        var handler = new Handler();
        var client = HttpTalentLibraryClient.Create(new ServerEndpoint(new Uri("http://localhost/"), "tok"), handler);
        var ct = TestContext.Current.CancellationToken;

        var entries = await client.ListAsync(ct);
        Assert.Equal("reviewer", Assert.Single(entries).TemplateId);
        Assert.Equal(2, (await client.VersionsAsync("reviewer", ct)).Count);
        Assert.Empty(await client.VersionsAsync("nope", ct));
        var diff = await client.DiffAsync("reviewer", 1, 2, ct);
        Assert.Equal("added", Assert.Single(diff!.PromptLines).Kind);
        Assert.Null(await client.DiffAsync("nope", 1, 2, ct));

        Assert.All(handler.Seen, r => Assert.Equal("Bearer tok", r.Auth));
        Assert.Contains(handler.Seen, r => r.Path == "/talent/templates/reviewer/diff?from=1&to=2");
    }

    [Fact]
    public async Task A_server_error_throws()
    {
        var client = HttpTalentLibraryClient.Create(new ServerEndpoint(new Uri("http://localhost/"), "tok"), new Handler { Fail = true });
        await Assert.ThrowsAsync<HttpRequestException>(async () => await client.ListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class Source : ITalentLibrarySource
    {
        public IReadOnlyList<LibraryEntry> Entries { get; set; } = [_entry];
        public IReadOnlyList<LibraryVersion> Versions { get; set; } = [_v1, _v2];
        public Dictionary<(int, int), LibraryDiff> Diffs { get; } = [];
        public List<(int, int)> Requested { get; } = [];
        public bool Throws { get; set; }

        public ValueTask<IReadOnlyList<LibraryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Throws ? throw new HttpRequestException("down") : new(Entries);

        public ValueTask<IReadOnlyList<LibraryVersion>> VersionsAsync(string templateId, CancellationToken cancellationToken) => new(Versions);

        public ValueTask<LibraryDiff?> DiffAsync(string templateId, int olderVersion, int newerVersion, CancellationToken cancellationToken)
        {
            Requested.Add((olderVersion, newerVersion));
            return new(Diffs.GetValueOrDefault((olderVersion, newerVersion)));
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public List<(string Path, string? Auth)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Seen.Add((path, request.Headers.Authorization?.ToString()));
            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            var (status, body) = path switch
            {
                "/talent/templates" => (HttpStatusCode.OK, """[{"templateId":"reviewer","name":"Reviewer","latestVersion":2,"standing":"released","toolCount":0,"versionCount":2}]"""),
                "/talent/templates/reviewer/versions" => (HttpStatusCode.OK, """[{"version":1,"contentSha":"a","submittedBy":"x","submittedAt":"1970-01-01T00:00:00+00:00","standing":"released"},{"version":2,"contentSha":"b","submittedBy":"x","submittedAt":"1970-01-01T00:00:00+00:00","standing":"quarantined"}]"""),
                "/talent/templates/reviewer/diff?from=1&to=2" => (HttpStatusCode.OK, """{"templateId":"reviewer","fromVersion":1,"toVersion":2,"oldName":null,"newName":null,"promptLines":[{"kind":"added","text":"x"}],"toolsAdded":[],"toolsRemoved":[],"modelsAdded":[],"modelsRemoved":[],"filesAdded":[],"filesRemoved":[],"needsNewToolGrant":false}"""),
                _ => (HttpStatusCode.NotFound, ""),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
