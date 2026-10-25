using System.Buffers;
using System.Text.Json.Serialization;

namespace Spike.S6.Wire;

public enum Kind { Req, Res, Evt, Err }

public readonly record struct Envelope(
    int V, string T, Kind K, string? Id, string? Re, string? S, long? N,
    DateTimeOffset Ts, ReadOnlyMemory<byte> P);

// Payload records are the shared contracts the JSON codec serialises directly.
public sealed record Cursor(string S, long After, long EditAfter);
public sealed record Hello(
    int VersionMin, int VersionMax, string[] Codecs,
    string DeviceId, string DeviceClass, string ClientBuild, Cursor[] Cursors);
public sealed record StreamRef(string S, long After, long EditAfter);
public sealed record Subscribe(StreamRef[] Streams);
public sealed record GcMessage(
    string GcId, long N, long Ev, string MessageId, string AuthorId, string Text,
    DateTimeOffset Ts, bool Edited, string[] Mentions);
public sealed record GcPage(GcMessage[] Items);
public sealed record Lagged(string S, long LastSent);
// Same shape plus one field added by a later minor version.
public sealed record GcMessageV11(
    string GcId, long N, long Ev, string MessageId, string AuthorId, string Text,
    DateTimeOffset Ts, bool Edited, string[] Mentions, string? ThreadId);

public interface IWireCodec
{
    string Name { get; }
    bool IsBinary { get; }
    void Encode(in Envelope e, IBufferWriter<byte> w);
    Envelope Decode(ReadOnlySpan<byte> frame);
    void EncodePayload<T>(T payload, IBufferWriter<byte> w);
    T DecodePayload<T>(in Envelope e);
}

[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(Subscribe))]
[JsonSerializable(typeof(GcMessage))]
[JsonSerializable(typeof(GcPage))]
[JsonSerializable(typeof(Lagged))]
[JsonSerializable(typeof(GcMessageV11))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
public sealed partial class WireJsonContext : JsonSerializerContext;
