using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Grouping;

namespace Bazaar.Diagnostics.Tests.Grouping;

public class IssueBodyTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private static IssueContent Content(string? code = "BZ-NET-0001", string? message = "failed", IReadOnlyList<FingerprintFrame>? frames = null) => new(
        GroupState.Create("fp1:0123456789abcdef0123456789abcdef", "0.1.0", _now) with { OccurrenceCount = 3 },
        code,
        "System.InvalidOperationException",
        frames ?? [new FingerprintFrame("System.Private.CoreLib", "System.Foo", "Bar", false), new FingerprintFrame("Bazaar.Client.Core", "Bazaar.Client.Core.Session", "Connect", true)],
        42,
        message,
        "Codex Alchemist company server");

    [Fact]
    public void Body_starts_with_the_fingerprint_marker()
    {
        var text = IssueBody.Render(Content());

        Assert.StartsWith("<!-- bazaar-crash-fp: fp1:0123456789abcdef0123456789abcdef -->", text.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_uses_the_error_code_when_there_is_one()
    {
        Assert.Equal("BZ-NET-0001 System.InvalidOperationException", IssueBody.Render(Content()).Title);
    }

    [Fact]
    public void Title_uses_the_type_and_flagged_frame_without_a_code()
    {
        var title = IssueBody.Render(Content(code: null)).Title;

        Assert.Equal("System.InvalidOperationException in Bazaar.Client.Core.Session.Connect", title);
    }

    [Fact]
    public void Body_names_the_filer_and_states_the_content_is_automated_and_redacted()
    {
        var body = IssueBody.Render(Content()).Body;

        Assert.Contains("Codex Alchemist company server", body, StringComparison.Ordinal);
        Assert.Contains("automated", body, StringComparison.Ordinal);
        Assert.Contains("redacted on the device", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_prints_the_flagged_frame_with_its_line_and_counts()
    {
        var body = IssueBody.Render(Content()).Body;

        Assert.Contains("`Bazaar.Client.Core Bazaar.Client.Core.Session.Connect` line 42", body, StringComparison.Ordinal);
        Assert.Contains("Occurrences: 3", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_top_five_frames_are_printed()
    {
        var frames = Enumerable.Range(0, 9).Select(i => new FingerprintFrame("M", "T", $"Method{i}", true)).ToArray();

        var body = IssueBody.Render(Content(frames: frames)).Body;

        Assert.Contains("Method4", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Method5", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_is_one_line_short_and_cannot_close_the_marker_or_break_the_code_span()
    {
        var hostile = "first --> line `x`\nsecond line " + new string('a', 500);

        var body = IssueBody.Render(Content(message: hostile)).Body;

        Assert.DoesNotContain("second line", body, StringComparison.Ordinal);
        var messageLine = body.Split('\n').Single(line => line.StartsWith("Message: ", StringComparison.Ordinal));
        Assert.True(messageLine.Length < IssueBody.MaxMessageLength + 30);
        Assert.DoesNotContain("-->", messageLine, StringComparison.Ordinal);
        Assert.Equal(2, messageLine.Count(ch => ch == '`'));
    }

    [Fact]
    public void Body_carries_no_logs_or_identifiers()
    {
        var body = IssueBody.Render(Content()).Body;

        Assert.DoesNotContain("install", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logs", body, StringComparison.OrdinalIgnoreCase);
    }
}
