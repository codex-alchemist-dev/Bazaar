using Bazaar.Client.Mail.Abstractions;

namespace Bazaar.Client.Mail.Tests;

public class MailFormatTests
{
    [Theory]
    [InlineData(MailQuarantineState.Held, "held")]
    [InlineData(MailQuarantineState.Released, "released")]
    [InlineData(MailQuarantineState.Purged, "purged")]
    [InlineData(MailQuarantineState.Reported, "reported")]
    public void Each_quarantine_state_has_its_word(MailQuarantineState state, string word) =>
        Assert.Equal(word, MailFormat.QuarantineState(state));

    [Fact]
    public void A_received_time_is_shown_in_utc_to_the_minute()
    {
        var received = DateTimeOffset.Parse("2026-11-16T22:05:00-05:00", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("2026-11-17 03:05 UTC", MailFormat.Received(received));
    }

    [Fact]
    public void A_link_with_no_host_reads_as_no_host_and_is_never_fetched()
    {
        // The formatter parses the text only. Nothing here opens a connection.
        Assert.Equal("no host", MailFormat.LinkHost("javascript:alert(document.cookie)"));
        Assert.Equal("no host", MailFormat.LinkHost(string.Empty));
    }
}
