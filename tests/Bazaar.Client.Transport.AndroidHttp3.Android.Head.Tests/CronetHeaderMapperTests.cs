namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head.Tests;

public class CronetHeaderMapperTests
{
    private static KeyValuePair<string, string> H(string n, string v) => new(n, v);

    [Fact]
    public void Request_headers_are_lower_cased_and_cronet_owned_ones_dropped()
    {
        var result = CronetHeaderMapper.ForRequest([H("Content-Type", "application/grpc"), H("Host", "x"), H("content-length", "5"), H("TE", "trailers"), H("bazaar-codec", "json")]);
        Assert.Equal([H("content-type", "application/grpc"), H("bazaar-codec", "json")], result);
    }

    [Theory]
    [InlineData(":path", "/x")]
    [InlineData("", "v")]
    [InlineData("bad name", "v")]
    [InlineData("x-ok", "a\r\nb")]
    [InlineData("x-ok", "a\0b")]
    public void Invalid_request_headers_throw(string name, string value) =>
        Assert.Throws<ArgumentException>(() => CronetHeaderMapper.ForRequest([H(name, value)]));

    [Fact]
    public void Response_headers_fold_repeats_in_order_and_skip_pseudo_headers()
    {
        var map = CronetHeaderMapper.ForResponse([H("Set-Cookie", "a"), H("set-cookie", "b"), H(":status", "200"), H("Grpc-Status", "0")]);
        Assert.Equal("a, b", map["set-cookie"]);
        Assert.Equal("0", map["grpc-status"]);
        Assert.False(map.ContainsKey(":status"));
    }
}
