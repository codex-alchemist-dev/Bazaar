using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>Reads numeric fields from an INFO memory reply, as the health probes do.</summary>
public class GarnetInfoParsingTests
{
    private const string _reply = "# Memory\r\nused_memory:1048576\r\nused_memory_human:1.00M\r\nmaxmemory:2097152\r\nmaxmemory_policy:noeviction\r\n";

    [Theory]
    [InlineData("used_memory", 1048576)]
    [InlineData("maxmemory", 2097152)]
    public void Reads_a_numeric_field_from_an_info_reply(string field, long expected)
    {
        Assert.Equal(expected, StackExchangeGarnetClient.InfoValue(_reply, field));
    }

    [Fact]
    public void Missing_field_reads_as_zero_and_does_not_match_a_prefix()
    {
        Assert.Equal(0, StackExchangeGarnetClient.InfoValue(_reply, "used"));
        Assert.Equal(0, StackExchangeGarnetClient.InfoValue("used_memory:not-a-number\r\n", "used_memory"));
        Assert.Equal(0, StackExchangeGarnetClient.InfoValue(string.Empty, "maxmemory"));
    }
}
