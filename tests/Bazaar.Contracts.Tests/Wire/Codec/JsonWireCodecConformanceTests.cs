using System.Text;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;

namespace Bazaar.Contracts.Tests.Wire.Codec;

/// <summary>The JSON codec, the floor, against the shared conformance suite (doc 90 Tests).</summary>
public class JsonWireCodecConformanceTests : WireCodecConformance
{
    protected override string ExpectedName => "json";

    protected override bool ExpectedIsBinary => false;

    protected override IWireCodec CreateCodec(WireDecodeLimits limits) => new JsonWireCodec(limits);

    protected override byte[] AddUnknownField(ReadOnlyMemory<byte> encodedObject, int nestingDepth)
    {
        var text = Encoding.UTF8.GetString(encodedObject.Span).TrimEnd();
        Assert.EndsWith("}", text);
        var body = text[..^1];
        var empty = body.TrimEnd().EndsWith('{');
        var nested = new StringBuilder();
        nested.Append('[', nestingDepth).Append('1').Append(']', nestingDepth);
        return Encoding.UTF8.GetBytes($"{body}{(empty ? "" : ",")}\"x-future\":{nested}}}");
    }
}
