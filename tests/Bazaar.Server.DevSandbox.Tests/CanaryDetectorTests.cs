using System.Text;

namespace Bazaar.Server.DevSandbox.Tests;

public class CanaryDetectorTests
{
    private const string _canary = "CANARY-7f3a9c-SECRET";

    [Fact]
    public void Find_NullText_Throws() =>
        Assert.Throws<ArgumentNullException>(() => CanaryDetector.Find(null!, [_canary]));

    [Fact]
    public void Find_NullCanaries_Throws() =>
        Assert.Throws<ArgumentNullException>(() => CanaryDetector.Find("text", null!));

    [Fact]
    public void Find_RawCanary_IsFound()
    {
        var found = CanaryDetector.Find($"POST /mail body={_canary}&x=1", [_canary]);

        Assert.Equal(_canary, found);
    }

    [Fact]
    public void Find_ReturnsFirstCanaryInListOrder()
    {
        var found = CanaryDetector.Find($"a {_canary} b {_canary}2", [_canary, $"{_canary}2"]);

        Assert.Equal(_canary, found);
    }

    [Fact]
    public void Find_Base64EncodedCanary_IsFound()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(_canary));

        var found = CanaryDetector.Find(encoded, [_canary]);

        Assert.Equal(_canary, found);
    }

    [Fact]
    public void Find_Base64EncodedCanaryWithSurroundingWhitespace_IsFound()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(_canary));

        var found = CanaryDetector.Find($"  {encoded}\n", [_canary]);

        Assert.Equal(_canary, found);
    }

    [Fact]
    public void Find_UrlEncodedCanary_IsFound()
    {
        var encoded = Uri.EscapeDataString($"{_canary} with spaces & symbols=");

        var found = CanaryDetector.Find($"q={encoded}", [$"{_canary} with spaces & symbols="]);

        Assert.Equal($"{_canary} with spaces & symbols=", found);
    }

    [Fact]
    public void Find_UrlEncodedCanaryWithPlainCharacters_IsFound()
    {
        var found = CanaryDetector.Find("q=CANARY%2D7f3a9c%2DSECRET", [_canary]);

        Assert.Equal(_canary, found);
    }

    [Fact]
    public void Find_NoCanary_ReturnsNull()
    {
        var found = CanaryDetector.Find("nothing to see here", [_canary]);

        Assert.Null(found);
    }

    [Fact]
    public void Find_CaseDifferentCanary_IsNotFound()
    {
        var found = CanaryDetector.Find(_canary.ToLowerInvariant(), [_canary]);

        Assert.Null(found);
    }

    [Fact]
    public void Find_EmptyText_ReturnsNull() =>
        Assert.Null(CanaryDetector.Find(string.Empty, [_canary]));

    [Fact]
    public void Find_EmptyCanaryList_ReturnsNull() =>
        Assert.Null(CanaryDetector.Find($"contains {_canary}", []));

    [Fact]
    public void Find_EmptyCanaryEntries_AreSkipped()
    {
        Assert.Null(CanaryDetector.Find("plain text without secrets", ["", string.Empty]));
    }

    [Fact]
    public void Find_EmptyCanaryEntryBeforeRealCanary_StillFindsRealCanary()
    {
        var found = CanaryDetector.Find($"leak {_canary}", ["", _canary]);

        Assert.Equal(_canary, found);
    }

    [Fact]
    public void Find_InvalidBase64Text_DoesNotThrow()
    {
        // Not valid base64 (length not a multiple of 4, bad characters) and not URL-encoded: no decode path, no match.
        Assert.Null(CanaryDetector.Find("abc!@#", [_canary]));
        Assert.Null(CanaryDetector.Find("abcde", [_canary]));
    }

    [Fact]
    public void Find_PercentSignWithoutValidEscape_DoesNotThrow()
    {
        Assert.Null(CanaryDetector.Find("100% sure, %zz not an escape", [_canary]));
    }

    [Fact]
    public void Find_OrdinaryTextThatBase64DecodesToGarbage_IsNotAFalsePositive()
    {
        // "aGVsbG8gd29ybGQ=" decodes to "hello world"; neither that nor the raw text contains the canary.
        var found = CanaryDetector.Find("aGVsbG8gd29ybGQ=", [_canary]);

        Assert.Null(found);
    }

    [Fact]
    public void Find_ShortCanaryInsideLongerWord_IsStillAMatch()
    {
        // Substring semantics: a canary embedded in a larger token counts as a leak.
        var found = CanaryDetector.Find($"prefix{_canary}suffix", [_canary]);

        Assert.Equal(_canary, found);
    }
}
