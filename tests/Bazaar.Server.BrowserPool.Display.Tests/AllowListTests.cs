using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.BrowserPool.Display.Tests;

public sealed class AllowListTests
{
    private static readonly string[] _expected =
    [
        "Input.dispatchKeyEvent",
        "Input.dispatchMouseEvent",
        "Input.insertText",
        "Page.captureScreenshot",
        "Target.attachToTarget",
        "Target.getTargets",
    ];

    [Fact]
    public void The_methods_the_session_can_name_are_exactly_the_allow_list()
    {
        var all = Enum.GetValues<CdpMethod>().Select(CdpMethods.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(_expected, all);
        Assert.Equal(_expected, CdpMethods.AllowList.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_value_outside_the_enum_has_no_name()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CdpMethods.Name((CdpMethod)999));
    }

    [Fact]
    public async Task The_methods_the_driver_actually_emits_across_every_operation_are_exactly_the_allow_list()
    {
        await using var browser = new FakeBrowser();
        var pipes = new FakePipes();
        pipes.Add("s", browser.Pipe);
        await using var driver = new PoolBrowserPageDriver(pipes);
        var ct = TestContext.Current.CancellationToken;

        await driver.CaptureScreenshotPngAsync("s", ct);
        await driver.DispatchInputAsync(
            "s",
            [
                new PointerMove(1, 1),
                new PointerButton(PointerButtonKind.Left, true),
                new PointerButton(PointerButtonKind.Left, false),
                new PointerClick(2, 2, PointerButtonKind.Right, 2),
                new PointerScroll(3, 3, 0, 100),
                new KeyStroke("Enter", KeyModifiers.Shift | KeyModifiers.Control),
                new TypeText("one\ntwo"),
            ],
            ct);

        var emitted = browser.Received.Select(c => c.Method).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(_expected, emitted);
    }

    [Fact]
    public async Task The_driver_emits_only_allow_listed_methods_even_when_the_browser_misbehaves()
    {
        await using var browser = new FakeBrowser();
        var pipes = new FakePipes();
        pipes.Add("s", browser.Pipe);
        browser.Handler = _ => Task.FromResult(FakeReply.Error(-32000));
        await using var driver = new PoolBrowserPageDriver(pipes);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<PageDriverException>(() => driver.CaptureScreenshotPngAsync("s", ct));
        await Assert.ThrowsAsync<PageDriverException>(() => driver.DispatchInputAsync("s", [new TypeText("x")], ct));

        Assert.All(browser.Received, c => Assert.Contains(c.Method, CdpMethods.AllowList));
    }

    [Fact]
    public void No_public_type_offers_a_way_to_send_an_arbitrary_method()
    {
        var publicTypes = typeof(PoolBrowserPageDriver).Assembly.GetExportedTypes().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();

        // Only the driver, its options, its component and its error types are public; the session, the method list and the translator are internal.
        Assert.Equal(["BrowserPageDriverOptions", "BrowserPoolDisplayComponent", "PageDriverException", "PageDriverFailure", "PoolBrowserPageDriver"], publicTypes);

        var driverMembers = typeof(PoolBrowserPageDriver).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["CaptureScreenshotPngAsync", "DispatchInputAsync", "DisposeAsync", "Supports"], driverMembers);
    }

    [Fact]
    public void The_module_references_only_the_two_port_projects_and_core()
    {
        var refs = typeof(PoolBrowserPageDriver).Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Bazaar.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["Bazaar.Core", "Bazaar.Core.Hosting", "Bazaar.Server.BrowserPool.Abstractions", "Bazaar.Server.VmDisplay.Abstractions"], refs);
    }
}
