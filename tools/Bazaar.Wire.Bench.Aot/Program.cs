using System.Buffers;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
#if CODEC_CBOR
using Bazaar.Wire.Codec.Cbor;
#elif CODEC_MSGPACK
using Bazaar.Wire.Codec.MessagePack;
#elif CODEC_PROTOBUF
using Bazaar.Wire.Codec.Protobuf;
#elif CODEC_MEMORYPACK
using Bazaar.Wire.Codec.MemoryPack;
#endif

#if CODEC_CBOR
IWireCodec codec = new CborWireCodec();
#elif CODEC_MSGPACK
IWireCodec codec = new MsgPackWireCodec();
#elif CODEC_PROTOBUF
IWireCodec codec = new ProtobufWireCodec();
#elif CODEC_MEMORYPACK
IWireCodec codec = new MemoryPackWireCodec();
#else
IWireCodec codec = new JsonWireCodec();
#endif

var message = new WireGcMessage(Guid.NewGuid(), 1, 1, Guid.NewGuid(), Guid.NewGuid(), "hello", DateTimeOffset.UtcNow, false, [], null);
var writer = new ArrayBufferWriter<byte>();
codec.Encode(new Envelope(1, WireTypes.GcMessage, EnvelopeKind.Event, null, null, null, 1, DateTimeOffset.UtcNow, codec.EncodePayload(message)), writer);
var back = codec.DecodePayload<WireGcMessage>(codec.Decode(writer.WrittenSpan));
Console.WriteLine($"{codec.Name} {writer.WrittenCount} bytes, text {back.Text}");
return back.Text == "hello" ? 0 : 1;
