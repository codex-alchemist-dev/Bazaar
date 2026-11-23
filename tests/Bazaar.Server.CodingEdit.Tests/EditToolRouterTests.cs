using Bazaar.Server.CodingEdit;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingEdit.Tests;

public sealed class EditToolRouterTests
{
    private readonly InMemoryWorktreeFiles _files = new();

    private EditToolRouter BuildRouter(EditMetrics metrics) =>
        new(
            [new WholeFileEditTool(_files), new LineAnchoredEditTool(_files), new SearchReplaceEditTool(_files)],
            metrics);

    [Fact]
    public async Task RoutesEachFormatToItsOwnTool()
    {
        _files.SetText("a.txt", "old\n");
        var router = BuildRouter(new EditMetrics());

        var result = await router.ApplyAsync(EditFormat.SearchReplace, new SearchReplaceEditRequest("a.txt", "old", "new"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("new\n", _files.GetText("a.txt"));
    }

    [Fact]
    public async Task RefusesARequestInTheWrongFormatForTheAgent()
    {
        _files.SetText("a.txt", "old\n");
        var router = BuildRouter(new EditMetrics());

        var result = await router.ApplyAsync(EditFormat.WholeFile, new SearchReplaceEditRequest("a.txt", "old", "new"), CancellationToken.None);

        Assert.Equal(EditRejectReason.Malformed, result.Reason);
        Assert.Equal("old\n", _files.GetText("a.txt"));
    }

    [Fact]
    public async Task RecordsEveryOutcomeInTheMetrics()
    {
        _files.SetText("a.txt", "x\n");
        var metrics = new EditMetrics();
        var router = BuildRouter(metrics);

        await router.ApplyAsync(EditFormat.WholeFile, new WholeFileEditRequest("a.txt", "y\n"), CancellationToken.None);
        await router.ApplyAsync(EditFormat.WholeFile, new WholeFileEditRequest("../out", "z"), CancellationToken.None);
        await router.ApplyAsync(EditFormat.SearchReplace, new WholeFileEditRequest("a.txt", "x"), CancellationToken.None);

        var snapshot = metrics.Snapshot();
        Assert.Equal(3, snapshot.Edits);
        Assert.Equal(1, snapshot.Applied);
        Assert.Equal(2, snapshot.Refused);
        Assert.Equal(1, snapshot.CountOf(EditRejectReason.PathEscape));
        Assert.Equal(1, snapshot.CountOf(EditRejectReason.Malformed));
        Assert.Equal(1.0 / 3.0, snapshot.MalformedRate, 6);
    }

    [Fact]
    public void MalformedRateIsZeroWithoutEdits()
    {
        Assert.Equal(0, new EditMetrics().Snapshot().MalformedRate);
    }

    [Fact]
    public void RejectsTwoToolsForTheSameFormat()
    {
        var files = new InMemoryWorktreeFiles();

        Assert.Throws<ArgumentException>(() => new EditToolRouter(
            [new WholeFileEditTool(files), new WholeFileEditTool(files)],
            new EditMetrics()));
    }

    [Fact]
    public void ToolsDeclareTheReadCapabilityClass()
    {
        Assert.Equal("Read", IEditTool.CapabilityClassName);
    }

    [Fact]
    public async Task DependencyInjectionComposesTheModule()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorktreeFiles>(_files);
        services.AddBazaarCodingEdit();
        using var provider = services.BuildServiceProvider();
        _files.SetText("b.txt", "one\n");

        var router = provider.GetRequiredService<EditToolRouter>();
        var result = await router.ApplyAsync(EditFormat.WholeFile, new WholeFileEditRequest("b.txt", "two\n"), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Same(router, provider.GetRequiredService<EditToolRouter>());
        Assert.Equal(3, provider.GetServices<IEditTool>().Count());
    }

    [Fact]
    public void EditFormatSettingParsesTheDocumentedValuesAndFallsBackToWholeFile()
    {
        Assert.True(EditFormatSetting.TryParse("line-anchored", out var anchored));
        Assert.Equal(EditFormat.LineAnchored, anchored);
        Assert.True(EditFormatSetting.TryParse(" Search-Replace ", out var replace));
        Assert.Equal(EditFormat.SearchReplace, replace);
        Assert.False(EditFormatSetting.TryParse("auto", out var fallback));
        Assert.Equal(EditFormatSetting.Default, fallback);
        Assert.Equal(EditFormat.WholeFile, EditFormatSetting.Default);
        Assert.Equal("harness.edit_format", EditFormatSetting.Key);
    }
}
