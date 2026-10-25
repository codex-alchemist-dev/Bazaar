using System.Buffers;
using System.Text;
using Google.Protobuf;
using P = Spike.S6.Wire.Proto;

namespace Spike.S6.Wire;

/// <summary>
/// Text frames from the same .proto: envelope as in the JSON codec, payload in the canonical proto3 JSON mapping.
/// Lets a non-.NET consumer without a protobuf runtime read the same schema as plain JSON.
/// </summary>
public sealed class ProtoJsonWireCodec : IWireCodec
{
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private readonly JsonWireCodec _envelope = new();

    public string Name => "protojson";
    public bool IsBinary => false;

    public void Encode(in Envelope e, IBufferWriter<byte> w) => _envelope.Encode(e, w);
    public Envelope Decode(ReadOnlySpan<byte> frame) => _envelope.Decode(frame);

    public void EncodePayload<T>(T payload, IBufferWriter<byte> w) =>
        w.Write(Encoding.UTF8.GetBytes(Formatter.Format(ProtoWireCodec.ToMessage(payload!))));

    public T DecodePayload<T>(in Envelope e)
    {
        string json = Encoding.UTF8.GetString(e.P.Span);
        IMessage m = typeof(T) switch
        {
            var t when t == typeof(Hello) => Parser.Parse<P.Hello>(json),
            var t when t == typeof(Subscribe) => Parser.Parse<P.Subscribe>(json),
            var t when t == typeof(GcMessage) => Parser.Parse<P.GcMessage>(json),
            var t when t == typeof(GcPage) => Parser.Parse<P.GcPage>(json),
            var t when t == typeof(Lagged) => Parser.Parse<P.Lagged>(json),
            var t when t == typeof(GcMessageV11) => Parser.Parse<P.GcMessageV11>(json),
            _ => throw new NotSupportedException(typeof(T).Name),
        };
        return (T)ProtoWireCodec.FromMessage(m);
    }
}
