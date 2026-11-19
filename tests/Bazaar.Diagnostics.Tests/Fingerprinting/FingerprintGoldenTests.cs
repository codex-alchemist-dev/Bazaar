using Bazaar.Diagnostics.Fingerprinting;

namespace Bazaar.Diagnostics.Tests.Fingerprinting;

/// <summary>
/// Golden tests for the fp1 algorithm (doc 43 section 8). Any change to the canonical input or the hash changes
/// these values, which is intended: existing groups are keyed by them.
/// </summary>
public class FingerprintGoldenTests
{
    private static FingerprintFrame App(string type, string method) => new("Bazaar.Client.Core", type, method, true);

    private static FingerprintFrame Framework(string module, string type, string method) => new(module, type, method, false);

    [Fact]
    public void Coded_failure_with_async_frame_matches_golden()
    {
        var input = new FingerprintInput(
            "BZ-GC-0042",
            "Bazaar.Client.Core.GcViewException",
            [
                App("Bazaar.Client.Core.Panes.GcView", "RenderPage"),
                Framework("Avalonia.Base", "Avalonia.Controls.Control", "Measure"),
                App("Bazaar.Client.Core.Wire.Pump", "<Run>d__12.MoveNext"),
            ],
            "ignored when a code is set");

        Assert.Equal(
            "fp1\nBZ-GC-0042\nBazaar.Client.Core.GcViewException\n" +
            "Bazaar.Client.Core|Bazaar.Client.Core.Panes.GcView|RenderPage\n" +
            "Bazaar.Client.Core|Bazaar.Client.Core.Wire.Pump|Run\n",
            Fingerprint.CanonicalInput(input));
        Assert.Equal("fp1:add6680a9a2ad6a2284994a24d3e1f29", Fingerprint.Compute(input));
    }

    [Fact]
    public void Uncoded_failure_uses_the_normalised_message()
    {
        var input = new FingerprintInput(
            null,
            "System.IO.FileNotFoundException",
            [new FingerprintFrame("Bazaar.Server", "Bazaar.Server.Files.Store", "Open(System.String)", true)],
            "Could not find 'C:\\Users\\alice\\x.txt' (id 3f2a9c1e-0b7d-4c2a-9e8f-123456789abc, attempt 7)\nsecond line");

        Assert.Equal(
            "fp1\n-\nSystem.IO.FileNotFoundException\n" +
            "Bazaar.Server|Bazaar.Server.Files.Store|Open\n" +
            "Could not find <str> (id <guid>, attempt <n>)\n",
            Fingerprint.CanonicalInput(input));
        Assert.Equal("fp1:5002e9b8e9315af199a9718730cceab5", Fingerprint.Compute(input));
    }

    [Fact]
    public void Generic_arity_and_local_functions_are_normalised()
    {
        var input = new FingerprintInput(
            "BZ-KANBAN-0007",
            "Bazaar.Server.Kanban.Board`1+MoveFailed",
            [
                Framework("System.Private.CoreLib", "System.Collections.Generic.List`1", "Add"),
                new FingerprintFrame("Bazaar.Server.Kanban", "Bazaar.Server.Kanban.Board", "<Board>g__Move|3_0", true),
            ],
            null);

        Assert.Equal(
            "fp1\nBZ-KANBAN-0007\nBazaar.Server.Kanban.Board+MoveFailed\n" +
            "Bazaar.Server.Kanban|Bazaar.Server.Kanban.Board|Move\n",
            Fingerprint.CanonicalInput(input));
        Assert.Equal("fp1:d09cf8c1967e1556d35e7a55b129fa18", Fingerprint.Compute(input));
    }

    [Fact]
    public void Without_app_frames_the_first_five_frames_are_used()
    {
        var frames = new[]
        {
            Framework("System.Runtime", "Sys.A", "M1"),
            Framework("System.Runtime", "Sys.A", "M2"),
            Framework("System.Runtime", "Sys.A", "M3"),
            Framework("System.Runtime", "Sys.A", "M4"),
            Framework("System.Runtime", "Sys.A", "M5"),
            Framework("System.Runtime", "Sys.A", "M6"),
        };

        var canonical = Fingerprint.CanonicalInput(new FingerprintInput(null, "System.Exception", frames, "Boom"));

        Assert.Equal(
            "fp1\n-\nSystem.Exception\n" +
            "System.Runtime|Sys.A|M1\nSystem.Runtime|Sys.A|M2\nSystem.Runtime|Sys.A|M3\n" +
            "System.Runtime|Sys.A|M4\nSystem.Runtime|Sys.A|M5\nBoom\n",
            canonical);
        Assert.Equal("fp1:c2094766c580af13b593886f47d16a8d", Fingerprint.Compute(new FingerprintInput(null, "System.Exception", frames, "Boom")));
    }

    [Fact]
    public void Parameter_lists_do_not_change_the_fingerprint()
    {
        var plain = new FingerprintInput("BZ-GC-0042", "Bazaar.Client.Core.GcViewException", [App("Bazaar.Client.Core.Panes.GcView", "RenderPage")], null);
        var withParameters = new FingerprintInput("BZ-GC-0042", "Bazaar.Client.Core.GcViewException", [App("Bazaar.Client.Core.Panes.GcView", "RenderPage(int, string)")], null);

        Assert.Equal(Fingerprint.Compute(plain), Fingerprint.Compute(withParameters));
    }

    [Fact]
    public void A_different_error_code_gives_a_different_fingerprint()
    {
        var frames = new[] { App("Bazaar.Client.Core.Panes.GcView", "RenderPage") };

        Assert.NotEqual(
            Fingerprint.Compute(new FingerprintInput("BZ-GC-0042", "T", frames, null)),
            Fingerprint.Compute(new FingerprintInput("BZ-GC-0043", "T", frames, null)));
    }

    [Fact]
    public void Fingerprint_has_prefix_and_32_lowercase_hex_characters()
    {
        var value = Fingerprint.Compute(new FingerprintInput(null, "T", [], null));

        Assert.Matches("^fp1:[0-9a-f]{32}$", value);
    }
}
