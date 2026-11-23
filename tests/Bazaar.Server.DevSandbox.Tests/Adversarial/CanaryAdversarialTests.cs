using System.Text;

namespace Bazaar.Server.DevSandbox.Tests.Adversarial;

public class CanaryAdversarialTests
{
    private const string _canary = "CANARY-ABC123-xyz";

    private static readonly string[] _all = [_canary];

    public static TheoryData<string, string> Encodings()
    {
        var bytes = Encoding.UTF8.GetBytes(_canary);
        var b64 = Convert.ToBase64String(bytes);
        var data = new TheoryData<string, string>
        {
            { "raw", _canary },
            { "raw embedded", "prefix {\"k\":\"" + _canary + "\"} suffix" },
            { "base64 whole", b64 },
            { "hex lower whole", Convert.ToHexStringLower(bytes) },
            { "hex upper whole", Convert.ToHexString(bytes) },
            { "hex embedded", "id=" + Convert.ToHexStringLower(bytes) + "&x=1" },
            { "hex embedded upper", "id=" + Convert.ToHexString(bytes) + "&x=1" },
            { "url encoded", Uri.EscapeDataString(_canary) },
            { "double url encoded", Uri.EscapeDataString(Uri.EscapeDataString(_canary)) },
            { "url encoded embedded", "q=" + Uri.EscapeDataString("x " + _canary) },
            { "base64 url-safe whole", b64.Replace('+', '-').Replace('/', '_').TrimEnd('=') },
        };
        foreach (var lead in new[] { "", "a", "ab" })
        {
            var embedded = Convert.ToBase64String(Encoding.UTF8.GetBytes(lead + _canary + "tail-data"));
            data.Add("base64 embedded in longer base64 (lead " + lead.Length + ")", "{\"blob\":\"" + embedded + "\"}");
            data.Add("base64 embedded alone (lead " + lead.Length + ")", "data:" + embedded);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Detected_encodings(string name, string text)
    {
        Assert.NotNull(name);
        Assert.Equal(_canary, CanaryDetector.Find(text, _all));
    }

    [Theory]
    [InlineData("canary-abc123-xyz")]
    [InlineData("CANARY-ABC123-xy")]
    [InlineData("nothing to see here")]
    public void Benign_text_is_not_flagged(string text)
    {
        Assert.Null(CanaryDetector.Find(text, _all));
    }

    [Fact]
    public void A_canary_split_across_two_texts_is_not_found_by_one_call_alone()
    {
        // Documented limit: the detector looks at one text. The runner joins consecutive payloads (see RunnerAdversarialTests).
        Assert.Null(CanaryDetector.Find(_canary[..8], _all));
        Assert.Null(CanaryDetector.Find(_canary[8..], _all));
        Assert.Equal(_canary, CanaryDetector.Find(_canary[..8] + _canary[8..], _all));
    }

    [Fact]
    public void Not_detected_today_utf16_and_reversed_text()
    {
        // Documented gaps, asserted so a change in behaviour is noticed.
        Assert.Null(CanaryDetector.Find(Convert.ToBase64String(Encoding.Unicode.GetBytes(_canary)), _all));
        Assert.Null(CanaryDetector.Find(new string(_canary.Reverse().ToArray()), _all));
    }
}
