using System.Text;
using Bazaar.Server.Browser.Cdp;

namespace Bazaar.Server.Browser.Tests.Cdp;

public sealed class CdpResponseTests
{
    private static bool Check(string json, int expectedId) =>
        CdpResponse.IsSuccess(Encoding.UTF8.GetBytes(json), expectedId);

    [Fact]
    public void MatchingIdWithResult_IsSuccess()
    {
        Assert.True(Check("""{"id":5,"result":{}}""", 5));
    }

    [Fact]
    public void MatchingIdWithoutResult_IsSuccess()
    {
        Assert.True(Check("""{"id":5}""", 5));
    }

    [Fact]
    public void ErrorMember_IsNotSuccess()
    {
        Assert.False(Check("""{"id":5,"error":{"code":-32000,"message":"nope"}}""", 5));
    }

    [Fact]
    public void ErrorMemberWithNull_IsNotSuccess()
    {
        Assert.False(Check("""{"id":5,"error":null}""", 5));
    }

    [Fact]
    public void DifferentId_IsNotSuccess()
    {
        Assert.False(Check("""{"id":6,"result":{}}""", 5));
    }

    [Fact]
    public void EventWithoutId_IsNotSuccess()
    {
        Assert.False(Check("""{"method":"Network.requestWillBeSent","params":{}}""", 5));
    }

    [Fact]
    public void NonIntegerId_IsNotSuccess()
    {
        Assert.False(Check("""{"id":"5","result":{}}""", 5));
    }

    [Fact]
    public void IdAfterNestedObjects_IsFound()
    {
        Assert.True(Check("""{"result":{"cookies":[{"name":"a"}],"deep":{"x":[1,2,{"y":null}]}},"id":4}""", 4));
    }

    [Fact]
    public void ErrorInsideResult_DoesNotCountAsErrorMember()
    {
        Assert.True(Check("""{"id":4,"result":{"error":"not top level"}}""", 4));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"id":5,"result":""")]
    [InlineData("""{"id":5 "result":{}}""")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("null")]
    public void MalformedInput_ReturnsFalseWithoutThrowing(string json)
    {
        Assert.False(Check(json, 5));
    }

    [Fact]
    public void InvalidUtf8_ReturnsFalseWithoutThrowing()
    {
        ReadOnlySpan<byte> bytes = [0x7B, 0xFF, 0xFE, 0x7D];

        Assert.False(CdpResponse.IsSuccess(bytes, 1));
    }

    [Fact]
    public void EmptySpan_ReturnsFalse()
    {
        Assert.False(CdpResponse.IsSuccess(ReadOnlySpan<byte>.Empty, 1));
    }
}
