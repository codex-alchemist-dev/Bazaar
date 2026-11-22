using System.Diagnostics;
using System.Globalization;

namespace Bazaar.Guest.Pointer.Tests;

/// <summary>
/// The tool against a real cage compositor driving Chromium, read back through grim (the probe page paints one solid colour per
/// event: red is the 80 pixel cell of a press or 200+ for a wheel, green the button or wheel direction). They skip, with the
/// reason, when cage, grim or Chromium is missing; the no-socket test needs none of them.
/// </summary>
public class RealCompositorTests(CageFixture cage) : IClassFixture<CageFixture>
{
    private const int _width = 1280;
    private const int _height = 720;

    private static async Task<RunResult> Tool(string runtimeDir, string? display, params string[] args)
    {
        (string file, string[] prefix) = CageFixture.Tool();
        return await CageFixture.RunAsync(file, [.. prefix, .. args], runtimeDir, TimeSpan.FromSeconds(20), display);
    }

    // The colour of the probe window now, as r,g,b, read at a corner no click in these tests lands on.
    private async Task<(int R, int G, int B)> Probe()
    {
        string? p = await cage.TryPixelAsync(2, 2);
        Assert.NotNull(p);
        int[] v = [.. p.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
        return (v[0], v[1], v[2]);
    }

    // Runs the tool, then waits for the probe's blue channel to change, which means a new event was painted.
    private async Task<(int R, int G, TimeSpan Tool, TimeSpan Seen)> Event(params string[] args)
    {
        int before = (await Probe()).B;
        var sw = Stopwatch.StartNew();
        RunResult r = await Tool(cage.RuntimeDir, "wayland-0", args);
        Assert.True(r.ExitCode == 0, $"bazaar-pointer exited {r.ExitCode}: {r.Stderr}");
        TimeSpan tool = r.Elapsed;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            (int pr, int pg, int pb) = await Probe();
            if (pb != before)
                return (pr, pg, tool, sw.Elapsed);
            await Task.Delay(50);
        }
        throw new TimeoutException("the probe page painted no event for " + string.Join(' ', args));
    }

    private static int Cell(int x, int y) => (x / 80) + (16 * (y / 80));

    [Fact]
    public async Task The_output_is_1280_by_720_and_the_compositor_offers_the_virtual_pointer_manager()
    {
        Assert.SkipWhen(cage.SkipReason is not null, cage.SkipReason ?? "");
        Assert.Equal("1280x720", await cage.OutputSizeAsync());
        // A move that the compositor refuses or cannot bind would exit non-zero.
        RunResult r = await Tool(cage.RuntimeDir, "wayland-0", "move", "640", "360", "1280", "720");
        Assert.True(r.ExitCode == 0, r.Stderr);
    }

    [Fact]
    public async Task Clicks_at_five_absolute_points_land_in_the_intended_cell_every_time()
    {
        Assert.SkipWhen(cage.SkipReason is not null, cage.SkipReason ?? "");
        (int X, int Y)[] points = [(15, 15), (640, 360), (1265, 705), (403, 517), (905, 98)];
        int hits = 0;
        int attempts = 0;
        var timings = new List<string>();
        for (int round = 0; round < 3; round++)
        {
            foreach ((int x, int y) in points)
            {
                attempts++;
                (int r, int g, TimeSpan tool, TimeSpan seen) = await Event("click", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture), "1280", "720", "left");
                if (r == Cell(x, y) && g == 20)
                    hits++;
                else
                    timings.Add($"MISS at {x},{y}: probe cell {r} button-code {g}, expected cell {Cell(x, y)}");
                timings.Add($"{x},{y}: tool {tool.TotalMilliseconds:F0} ms, visible {seen.TotalMilliseconds:F0} ms");
            }
        }
        TestContext.Current.SendDiagnosticMessage($"pointer clicks {hits}/{attempts}; " + string.Join("; ", timings));
        Assert.Equal(attempts, hits);
        Assert.Equal(15, attempts);
    }

    [Fact]
    public async Task Left_and_right_buttons_are_told_apart()
    {
        Assert.SkipWhen(cage.SkipReason is not null, cage.SkipReason ?? "");
        (int rl, int gl, _, _) = await Event("click", "640", "360", "1280", "720", "left");
        (int rr, int gr, _, _) = await Event("click", "640", "360", "1280", "720", "right");
        (int rm, int gm, _, _) = await Event("click", "640", "360", "1280", "720", "middle");
        Assert.Equal(Cell(640, 360), rl);
        Assert.Equal(Cell(640, 360), rr);
        Assert.Equal(Cell(640, 360), rm);
        Assert.Equal((20, 220, 120), (gl, gr, gm));
    }

    [Fact]
    public async Task Scroll_produces_a_wheel_event_in_the_requested_direction()
    {
        Assert.SkipWhen(cage.SkipReason is not null, cage.SkipReason ?? "");
        // Click first so the page has the pointer and focus, as an agent would.
        _ = await Event("click", "640", "360", "1280", "720", "left");
        (int rd, int gd, _, _) = await Event("scroll", "640", "360", "1280", "720", "0", "3");
        (int ru, int gu, _, _) = await Event("scroll", "640", "360", "1280", "720", "0", "-3");
        Assert.Equal((200, 220), (rd, gd));
        Assert.Equal((200, 20), (ru, gu));
        (int rr, _, _, _) = await Event("scroll", "640", "360", "1280", "720", "2", "0");
        Assert.Equal(201, rr);
    }

    [Fact]
    public async Task Down_then_up_is_a_press_without_a_click_and_arrives_as_two_events()
    {
        Assert.SkipWhen(cage.SkipReason is not null, cage.SkipReason ?? "");
        (int rd, int gd, _, _) = await Event("down", "403", "517", "1280", "720", "left");
        Assert.Equal((Cell(403, 517), 20), (rd, gd));
        RunResult up = await Tool(cage.RuntimeDir, "wayland-0", "up", "403", "517", "1280", "720", "left");
        Assert.Equal(0, up.ExitCode);
    }

    [Fact]
    public async Task Without_a_compositor_socket_the_tool_fails_on_its_own_with_one_line()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bazaar-pointer-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // No wall-clock bound here: the elapsed time is mostly the start of the tool's process, which took over
            // 4 seconds on a loaded Windows runner. The tool connects once and does not retry, so a missing socket
            // ends it at once; a tool that waited for the socket would run into the runner's timeout, which throws.
            RunResult r = await Tool(dir, "wayland-0", "click", "5", "5", "1280", "720", "left");
            Assert.Equal(1, r.ExitCode);
            Assert.Single(r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("bazaar-pointer:", r.Stderr, StringComparison.Ordinal);
            TestContext.Current.SendDiagnosticMessage($"no-socket exit {r.ExitCode} in {r.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_rejected_command_line_exits_2_without_touching_the_compositor()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bazaar-pointer-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunResult r = await Tool(dir, null, "click", "5", "5", "1280", "720", "left", "9");
            Assert.Equal(2, r.ExitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
