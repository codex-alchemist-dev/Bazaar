# Wire Transports and Codecs

Status: DECIDED (owner, 2026-11-19: modular transports and codecs; JSON over WebSocket stays) · PROPOSED (everything below the decided list) · Owner questions: 4 (all answered)
Depends on: 02-wire-protocol-and-sync.md, 39-conduit-remote-connectivity.md, 40-core-feature-modules-audit.md, 42-deps-as-modules.md, 15-security-taint-and-approvals.md, 32-remote-access-auth-and-privacy.md, 03-server-host-and-providers.md · Used by: 02, 27, 29, 39, Bazaar.Sdk · Spike: `docs/spikes/S-WTX.md` (to be written; plan in section 10)

## Purpose
Doc 02 fixes the envelope, the streams and the sync rules, and ships one transport (WebSocket) and one codec (JSON). This doc makes both pluggable. Every other transport and every other payload format is its own optional module that an installer picks. The wire protocol does not change: the same envelope, the same message types, the same replay and idempotency rules run over any transport and in any codec.

## Decided inputs
- **Owner, 2026-11-19:** keep JSON over WebSocket as it is. Add every other transport and payload format as separate optional modules. Transports: HTTP/3 over QUIC (`System.Net.Quic`), WebTransport (for browsers), gRPC with gRPC-Web, next to the existing WebSocket on HTTP/1.1 and HTTP/2. Codecs: MemoryPack, CBOR (RFC 8949), MessagePack and Protobuf (for gRPC), and any other worthwhile ones.
- **The old "no Protobuf tooling" rule (CLAUDE.md, DECISIONS-LOG round 15, S6) is lifted for the gRPC module and for the Protobuf codec module only.** Core, Contracts and the JSON path stay free of Protobuf.
- One set of C# contract types is the single source. Every codec is generated from it. A published schema serves outside clients.
- Every transport and every codec is its own module with an `.Abstractions` project where another module needs one. Core gets no code (CLAUDE.md). Providers are plain modules: every installed one loads and the consuming module (the wire connection layer) decides (#919).
- Doc 02's rules stay: the server is reached over one authenticated connection per device; GCs are append-only; cursors `(n, ev)` are authoritative on the client; a retried request with the same `id` returns the original result.

## Design

### 1. Vocabulary
- **Transport:** moves whole frames between one client and one server, with a credential check at connect time, reliable and ordered per connection. Examples: WebSocket, native QUIC stream, WebTransport stream, gRPC stream.
- **Codec:** turns an `Envelope` plus payload records into bytes and back (`IWireCodec`, unchanged).
- **Transform (optional):** a reversible step on the frame bytes after the codec, such as compression. Section 7.
- **Wire profile:** one chosen triple `(transport, codec, transform?)` for a connection.
- **Floor:** WebSocket plus JSON. Compiled into the baseline, always available for loopback and recovery, and the common denominator when nothing else matches.

### 2. Module catalogue [PROPOSED]
Names follow the module sides rule (docs 40 and `BAZAAR-COMPONENTS.md`). Codecs are shared pure libraries used identically by both sides, so they are one module each of `kind: library`; transports have a server half and a client half.

| Module | Role | Notes |
|---|---|---|
| `Bazaar.Wire.Abstractions` | `IWireTransportListener`, `IWireTransportConnector`, `IWireConnection`, `WireProfile`, `ITransportOffer`, `IWireTransform`, framing helpers, negotiation types | BCL only. Takes over `IWireTransportFactory` and `IWireConnection` from `Bazaar.Client.Core/Wire`. |
| `Bazaar.Wire.Generators` | Roslyn incremental generator and analyzer (build-time only; netstandard2.0, ships as an analyzer) | Reads `[WirePayload]` records in `Bazaar.Contracts`; emits the type registry and per-codec adapters. Built for `cbor` and `msgpack`; schema artifacts (section 5), the `schema.lock.json` gate and the other codecs are still to do. A codec project opts in with `[assembly: GenerateWireCodec("cbor")]` or `"msgpack"`. Both targets share one type resolver and one record analysis. |
| `Bazaar.Wire.Conformance` | Test library | One conformance suite per transport and per codec, reused by every module's tests. The codec half is built: a test project inherits `WireCodecConformance` and supplies the codec plus a hook that adds an unknown field; it runs against `JsonWireCodec`, `CborWireCodec` and `MsgPackWireCodec`. The transport half is still to do. |
| JSON codec | Stays in `Bazaar.Contracts` as `JsonWireCodec` for now | The floor. Moves to `Bazaar.Wire.Codec.Json` only if doc 40 asks; no behaviour change. |
| `Bazaar.Wire.Codec.MemoryPack` | `memorypack` | .NET to .NET only. Fastest expected. |
| `Bazaar.Wire.Codec.Cbor` | `cbor` | `System.Formats.Cbor` (a NuGet package from the .NET team, 11.0.0; it is not in the shared framework, so the module takes one package dependency). Self-describing, standard, good for outside clients. Built (WP2); shape in section 3.1. |
| `Bazaar.Wire.Codec.MessagePack` | `msgpack` | `MessagePack` (MessagePack-CSharp 3.1) used only through its low-level `MessagePackReader` and `MessagePackWriter`, called from generated code; no resolver, no typeless mode, no reflection. Built (WP3); shape in section 3.2. |
| `Bazaar.Wire.Codec.Protobuf` | `protobuf` | Generated `wire.proto`; used by gRPC and available to any transport. |
| `Bazaar.Wire.Generators` | Roslyn incremental generator and analyzer (build-time only; netstandard2.0, ships as an analyzer) | Reads `[WirePayload]` records in `Bazaar.Contracts`; emits the type registry and per-codec adapters. Built for `cbor` and `protobuf` (the `wire.proto` text, a drift test, the mapper); the other schema artifacts (section 5), the `schema.lock.json` gate and the other codecs are still to do. A codec project opts in with `[assembly: GenerateWireCodec("cbor")]`. |
| `Bazaar.Wire.Conformance` | Test library | One conformance suite per transport and per codec, reused by every module's tests. The codec half is built: a test project inherits `WireCodecConformance` and supplies the codec plus a hook that adds an unknown field; it runs against `JsonWireCodec`, `CborWireCodec` and `ProtobufWireCodec`. The transport half is still to do. |
| JSON codec | Stays in `Bazaar.Contracts` as `JsonWireCodec` for now | The floor. Moves to `Bazaar.Wire.Codec.Json` only if doc 40 asks; no behaviour change. |
| `Bazaar.Wire.Codec.MemoryPack` | `memorypack` | .NET to .NET only. Fastest expected. |
| `Bazaar.Wire.Codec.Cbor` | `cbor` | `System.Formats.Cbor` (a NuGet package from the .NET team, 11.0.0; it is not in the shared framework, so the module takes one package dependency). Self-describing, standard, good for outside clients. Built (WP2); shape in section 3.1. |
| `Bazaar.Wire.Codec.MessagePack` | `msgpack` | Generated formatters only; no typeless mode. |
| `Bazaar.Wire.Codec.Protobuf` | `protobuf` | `Google.Protobuf` 3.36.2 and `Grpc.Tools` 2.85.0 (these two packages only here; the gRPC module adds its own). Generated `wire.proto`, checked in at `docs/wire/wire.proto`; used by gRPC and available to any transport. Built (WP2); shape in section 3.2. |
| `Bazaar.Server.Transport.WebSocket` / `Bazaar.Client.Transport.WebSocket` | `ws` | The existing WebSocket moved out of `Bazaar.Server.Wire` (`UseBazaarWire`) and `Bazaar.Client.Core/Wire/WebSocketWireTransport.cs`. Behaviour identical. HTTP/1.1, HTTP/2 (RFC 8441) and, when `Http3` is on, HTTP/3 (RFC 9220). |
| `Bazaar.Server.Transport.Http3` | `h3` listener feature | Adds HTTP/3 (QUIC) bindings and `Alt-Svc` to the existing listeners so `ws`, `grpc` and `webtransport` are reachable over HTTP/3. Not a transport itself. |
| `Bazaar.Server.Transport.QuicNative` / `Bazaar.Client.Transport.QuicNative` | `quic` | Raw `System.Net.Quic`, ALPN `bazaar-wire/1`, no HTTP. Settles the QUIC framing question that doc 39 left open for `p2p`. |
| `Bazaar.Client.Transport.AndroidHttp3` | `grpc` over HTTP/3 on Android | Owner, 2026-11-19. Client-only module (Android head). Uses Cronet (Chromium network stack, QUIC and HTTP/3 built in, no MsQuic needed) through its bidirectional stream API, and speaks the same gRPC framing as `grpc` to the server's existing `grpc` endpoint, so the server needs only the `Grpc` and `Http3` modules. Tried first on Android; any failure falls back to `grpc` over HTTP/2, then `ws`. |
| `Bazaar.Server.Transport.WebTransport` (+ `Bazaar.Client.Transport.WebTransport` if the spike allows) | `webtransport` | For browsers and other outside clients. A .NET client uses `quic`. |
| `Bazaar.Server.Transport.Grpc` / `Bazaar.Client.Transport.Grpc` | `grpc`, `grpc-web` | `Grpc.AspNetCore`, `Grpc.Net.Client`, `Grpc.AspNetCore.Web`. Requires the Protobuf codec module. |
| `Bazaar.Server.Wire.Discovery` | `/.well-known/bazaar/wire` and the schema routes | Read-only, unauthenticated, allowed on the status surface (section 6). |
| `Bazaar.Wire.Transform.Zstd` (later) | `zstd` | Section 7. |

Candidates evaluated in the spike and built only if they pass the conformance suite and stay AOT-clean: FlatBuffers, Apache Avro, Bebop, Cap'n Proto, Apache Arrow IPC (for bulk table-shaped payloads such as Constellation snapshots), Brotli and Zstd as transforms. Each would be one more module with no change to anything else.

Dependency rules, enforced by `tests/Bazaar.Architecture.Tests`: a transport or codec module references only `Bazaar.Wire.Abstractions`, `Bazaar.Contracts` and its own third-party packages; no module references `Bazaar.Server.csproj`; transports never reference codecs (the connection layer joins them); the gRPC module hard-depends on the Protobuf codec module, nothing else has a hard edge to a codec. A missing optional module only removes its own entries from negotiation.

### 3. Contracts: one source, generated for every codec [PROPOSED]
`Bazaar.Contracts` stays plain records (S6 rule: camelCase names, explicit nullables, no polymorphic or tuple payloads, no codec attributes, no codec packages). Two additions, both dependency-free:

1. `[WirePayload("gc.message")]` marks a record as a wire payload and gives its message type. This replaces the hand-kept type lists. The attribute allows several uses on one record (a record shared by two message types, such as the pause and resume requests) and has a parameterless form for a reply or a shape shared between messages that has no message type of its own. Records that are only nested inside a payload need no marker; the generator follows property types. The `JsonSerializable` list in `WireJson.cs` stays until the JSON context is itself generated.
2. Field identity for schema-first codecs: positional order is the field number (1-based), append-only. The analyzer fails the build on reordering, removal or a type change of a published field, and on a new field that is not nullable or defaulted. A checked-in `docs/wire/schema.lock.json` is regenerated by the generator and diffed in CI; a breaking diff needs a protocol major bump (doc 02 versioning).

`Bazaar.Wire.Generators` emits per codec module, from the same records:
- JSON: the existing `System.Text.Json` source-generated context.
- CBOR: direct `CborWriter`/`CborReader` code, map keys equal to the JSON camelCase names (not short names: one name set for JSON, CBOR and the published schema), no reflection. Section 3.1 gives the shape.
- MessagePack: direct `MessagePackWriter`/`MessagePackReader` code over the contract records themselves, so no mirror DTOs are needed and Contracts never sees the package (section 3.2).
- MemoryPack: mirror DTOs carrying the codec's attributes plus both-way mappers, generated into the codec assembly so Contracts never sees the package. Mirrors use the codec's version-tolerant mode with explicit field order.
- Protobuf: `wire.proto` and mirror messages plus mappers (the S6 mapping layer, now generated instead of hand-written).
- MessagePack and MemoryPack: mirror DTOs carrying the codec's attributes plus both-way mappers, generated into the codec assembly so Contracts never sees the package. Mirrors use the codec's version-tolerant mode with explicit field order.
- Protobuf: `wire.proto` and mirror messages plus mappers (the S6 mapping layer, now generated instead of hand-written). The generator emits the mapper as C# and the schema text; protoc (`Grpc.Tools`) builds the mirror messages from the checked-in file. Section 3.2 gives the shape.

Every generated path is AOT and trim clean (`IsAotCompatible`, warnings as errors). The Protobuf runtime's IL3050 and IL3053 warnings from S6 (Google.Protobuf 3.33.0) get one documented suppression inside the Protobuf codec module only, if they appear. With the pinned 3.36.2 they do not: neither the module build nor a Native AOT publish (linux-x64) of an app using the codec reports them, so the module suppresses nothing today and its project file says where a suppression would go.

Forward and backward rules, identical for every codec and pinned by the conformance suite: unknown envelope and payload fields are ignored; an unknown message type leaves `p` opaque so the dispatcher answers `err unsupported` without decoding it; a field absent from an older sender reads as null or default; a decoder never throws on unknown enum values when the enum has a fallback member: it maps to the member named `Unknown`, or `Unspecified` when there is no `Unknown`. An enum with neither (such as `PrincipalKind`) throws `WireFormatException` for a value it does not know, because guessing an identity kind would be worse than refusing the frame.

### 3.1 CBOR shape [DECIDED in code, WP2]
- Envelope: one definite-length map with the doc 02 field names `v`, `t`, `k`, `id`, `re`, `s`, `n`, `ts`, `p`; absent optional fields are omitted. `k` is the text `req`, `res`, `evt` or `err`; `ts` is a tag 0 date-time string (UTC).
- `p` is the payload's own CBOR data item embedded in the envelope map, not a byte string wrapped around it. A decoder finds its extent with a depth-limited skip and keeps the bytes unparsed until `DecodePayload`, so an unknown message type costs no payload decoding and reaches the dispatcher byte for byte.
- Payload: a map with text keys equal to the JSON property names; null fields are omitted (same as JSON). `Guid` is the text form `D`, `DateTimeOffset` a tag 0 string, `decimal` a tag 4 decimal fraction, integers are CBOR integers. `float[]` and `ReadOnlyMemory<float>` are one RFC 8746 typed array: tag 85 (little-endian IEEE 754 binary32) over a byte string. Enums are text: the `JsonStringEnumMemberName` value when present, else the camelCase member name (this differs from JSON, which writes the numeric value for enums without a converter; text is self-describing and lets an unknown value fall back, see section 3).
- Reader and writer use `CborConformanceMode.Strict`: duplicate map keys, non-text keys, invalid UTF-8 and trailing bytes after the item are `WireFormatException`.
- A missing field reads as default for value types, null for nullable types, and the constructor default when the record declares one; a missing non-nullable reference field (such as `text`) is a `WireFormatException`.
- Every failure of a decoder, including a record constructor refusing values, surfaces as `WireFormatException`; an unregistered `T` is `NotSupportedException`.


### 3.2 MessagePack shape [DECIDED in code, WP3]
Same field names, omission rule, limits and error type as 3.1, over the MessagePack spec instead of CBOR.
- Envelope: one map with the doc 02 field names; absent optional fields are omitted; `k` is the text `req`, `res`, `evt` or `err`. `ts` is the MessagePack timestamp extension (type -1, UTC instant). `p` is the payload's own MessagePack item embedded in the map, skipped with a depth-limited pass and kept as bytes until `DecodePayload`.
- Payload: a map with string keys equal to the JSON property names; null fields omitted. `Guid` is the text form `D`. `DateTimeOffset` is the ISO 8601 round-trip text (`O`), so a payload keeps its offset exactly as JSON does (the envelope `ts` is an instant, so it uses the timestamp extension). `decimal` is its invariant text, because MessagePack has no decimal type. Integers are MessagePack integers. `float[]` and `ReadOnlyMemory<float>` are one `bin` of little-endian binary32 values. Enums are text, as in 3.1.
- Strictness: duplicate map keys, non-string keys, invalid UTF-8, the never-used type code `0xC1`, an integer that does not fit its field and data after the item are all `WireFormatException`. Strings and `bin` items are measured against the byte cap before they are decoded or copied.
- Library use: only `MessagePackReader` and `MessagePackWriter`. Typeless mode, resolvers and `MessagePackSerializer` are never called; the generated code is the only formatter.
### 3.2 Protobuf shape [DECIDED in code, WP2]
Codec name `protobuf`, binary frames. `Bazaar.Wire.Codec.Protobuf` references `Bazaar.Contracts`, the generator and its two packages only.
- **Schema.** `docs/wire/wire.proto` is generated from the `[WirePayload]` records (field number = 1-based property position, append-only; one flat message per record; no `Any`, extensions, groups or imports) and checked in, because protoc must read it at build time. `tests/Bazaar.Wire.Generators.Tests` fails when the file differs from the generator output; `BAZAAR_UPDATE_WIRE_PROTO=1 dotnet test tests/Bazaar.Wire.Generators.Tests` rewrites it. A second test compares the copy compiled into the codec. After a record change the codec project does not build until the file is refreshed.
- **Mirror messages and mappers.** protoc builds internal mirror messages (`Bazaar.Wire.Codec.Protobuf.Proto`); the generator emits both-way mappers between them and the records (`WireProtobuf.g.cs`). Contracts never sees the packages (an architecture test pins that only the Protobuf and gRPC modules reference them).
- **Type mapping.** `Guid` is 16 bytes in RFC 4122 order; `DateTimeOffset` is an `Instant` message (UTC ticks plus offset in minutes, so the offset survives); `decimal` is its invariant text; `byte` is `uint32` and must fit; enums are proto enums carrying the C# numeric values (a zero member comes first; an enum without one gets a `_NONE = 0`), with the same fallback rule as section 3; a nullable scalar is `optional`; a nullable list is wrapped in a `<Element>List` message so that null differs from empty; lists of lists and of nullable items are refused by the generator (BWG007).
- **Envelope.** The `Envelope` message in `wire.proto`: `v t k id re s n ts p` and a closing `end = true` (field 10). Plain Protobuf accepts a message cut at a field boundary and merges two messages glued together, so the decoder is stricter than protoc here: known envelope fields must be ascending and unique, field types must match, and `end` must be present, otherwise `WireFormatException`. Unknown fields may appear anywhere.
- **Empty payload.** A message with no set field is zero bytes, which `Envelope.p` cannot tell from "no payload", so such a payload is written as `F8 FF FF FF 0F 00` (field 536870911, varint 0). Every reader skips it as an unknown field.
- **Missing fields.** Protobuf has no required fields: an absent scalar or string reads as its proto3 default (unlike CBOR, which refuses a missing non-nullable `text`), an absent optional field is null, an absent nested record message is a `WireFormatException`.
- **Limits.** Depth counts the envelope as level 1, the payload record as level 2 and each nested record or list as one more, so the numbers mean the same as in CBOR; the protobuf recursion cap (groups in unknown fields) is set from the same limit. String length is checked in bytes after decode, list length by count. Decode failures surface only as `WireFormatException`.

### 4. Negotiation [PROPOSED]
Two questions, answered in this order: which transport reaches the server, then which codec runs over it.

**4.1 Transport selection (client side).**
1. The client knows an endpoint from Conduit (doc 39: address, trust pin). Conduit decides how to reach the host; the transport list says what the host speaks. They do not overlap.
2. After the first successful connection the client caches the server's `TransportOffer` list from `welcome` (new capability field, section 4.3) and refreshes it from `/.well-known/bazaar/wire` when reachable. On first contact it tries the floor, then upgrades on the next connect.
3. The client orders usable offers by its own preference (`wire.client.transport_order`, default `quic, h3-grpc, webtransport, grpc, ws` (`h3-grpc` is the Android HTTP/3 module), filtered to modules installed in this client). If the best offer is UDP-based, it races it against the best TCP-based offer: start the UDP attempt, start the TCP attempt after 250 ms, keep the first that completes `hello`/`welcome`, close the other.
4. A failed offer is remembered for 10 minutes (exponential up to 1 hour) so UDP-hostile networks do not pay the delay on every reconnect. Success clears it.
5. `h3` is discovered through `Alt-Svc` for the HTTP-based transports in the usual way; the client never assumes it.

**4.2 Codec selection.**
- *Mode A, at connect (preferred):* the codec rides the transport's own negotiation, so `hello` is already in that codec and nothing is decoded before it is known: WebSocket `Sec-WebSocket-Protocol: bazaar.v1.<codec>, ...` in preference order; gRPC `content-type` and a `bazaar-codec` metadata key; WebTransport and QUIC a preface frame `0xBA 0x5A <len> <codec names>`. The server picks the first name it also has enabled and echoes it.
- *Mode B, in `hello` (today's behaviour, kept for old clients):* no codec at connect means the floor codec for `hello`; `Hello.Codecs` lists what the client can decode, `Welcome.Codec` picks one, and every frame after `welcome` uses it. An old client never sends a subprotocol and keeps working untouched.
- The chosen codec must be one the client offered and the operator enabled. A binary frame before the codec is known, or in a codec that was not chosen, closes with 4400.

**4.3 What the server publishes.** `Welcome.Capabilities` gains `transports:ws,quic,...` and `codecs:json,cbor,...` flags, and a new optional `Welcome.Offers` list of `{ transport, url or host:port, alpn, codecs }`. Clients ignore what they do not know (existing rule). The same data is served unauthenticated from the discovery route (section 6) with no secret and no per-user data.

**4.4 Downgrade and tamper resistance.**
- Negotiation runs inside TLS, so a network attacker cannot rewrite it. The plaintext overlay case (`AllowPlaintextOverlay`, doc 02) is the exception, so `welcome` echoes a SHA-256 of the client's offered transport and codec lists; a client that sees a different hash closes and logs.
- The server never picks something the client did not offer, and the operator's allow lists (section 8) apply after the client's list, so a client cannot force a codec the operator turned off.
- Floors cannot be loosened by settings (doc 02, round 13): `ws`+`json` stay available on loopback and the admin recovery path; an operator can drop them from public listeners only while at least one other transport and codec pair works, and the setting UI shows the clients that would lose access.

**4.5 Per-connection limits for every codec.**
- `MaxFrameBytes` is checked on the byte length before any decode (transport level for QUIC and gRPC message size limits, WebSocket message size).
- Pre-`hello` traffic is capped at 8 KiB and 5 s (`wire.hello_timeout_s`), one frame, and must be a `hello`.
- Nesting depth 32, string 1 MiB, collection length 100 000 (operator-tightenable, never loosenable), enforced in the generated decoders for every codec, not left to the library default. The numbers live in `WireDecodeLimits` (`Bazaar.Contracts`); every codec takes one in its constructor. Depth counts the envelope map as level 1, so a payload sits at level 2. The JSON codec is not generated: `JsonWireCodec` runs a single-pass scan (`JsonLimitScanner`) over each frame and each payload before System.Text.Json sees it, which also rejects data after the root value. The CBOR reader counts as it reads and also counts a chain of tags against the depth cap.
- No polymorphic or type-name-driven deserialization anywhere: MessagePack typeless mode, MemoryPack unions and `Type` fields, Protobuf `Any` are banned by the analyzer. Payload type comes only from the envelope `t`, looked up in the generated registry (the closed set).
- 0-RTT and TLS early data are off; auth-bearing frames are never replayable.
- Binary codecs get a fuzz target each (random, mutated and truncated frames, 10 minutes in CI nightly) and must only fail with `WireFormatException`.

### 5. Published schema for outside clients [PROPOSED]
The generator publishes, from the same records, per build:
- `wire.proto` (for `prost`, `grpc` tooling and any language),
- JSON Schema for the JSON and CBOR shapes,
- CDDL for CBOR,
- `schema.json` (message types, fields, numbers, protocol version, schema hash),
- golden frames per codec (`docs/wire/vectors/<codec>/*.bin` plus a text description) that a foreign client must round-trip.

They ship in release artifacts and are served by `Bazaar.Server.Wire.Discovery`. The schema hash is part of `welcome`; a client that sees a hash it does not know still works (forward-compatible rules) and may log. Outside clients are a supported path now (owner), not only a fallback; the Rust note in S6 stays valid.

### 6. Discovery surface [PROPOSED]
`GET /.well-known/bazaar/wire` returns the transport offers, enabled codecs, protocol range, limits that are public, and the schema hash. `GET /.well-known/bazaar/wire/schema/{name}` returns the artifacts above. Read-only, cacheable, rate-limited under the status bucket, allowed on the browser status surface (doc 02 permits a read-only status page). It reveals no user, device or secret data and nothing the `welcome` of an unauthenticated probe would not reveal. Operators can turn it off; the client then relies on `welcome`.

### 7. Transforms [PROPOSED]
Compression is a transform module, negotiated like a codec (`Hello.Transforms`, Mode A header `bazaar-transform`). Rules: off by default; never applied to `hello`, `welcome`, `auth.refresh` or any frame carrying a credential (BREACH and CRIME style leaks mix secrets with attacker-influenced data); decompressed size is capped by `MaxFrameBytes` before inflating (compression-bomb guard), and a ratio above 100:1 closes with 4400. WebSocket `permessage-deflate` stays off.

### 8. Settings [PROPOSED]
Registered in doc 03's settings registry; operator-set, install scope; every change audited; settings may tighten and never loosen the floors (round 13).

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `wire.transports.enabled` | string list | all installed | install | Operator | No; audited |
| `wire.codecs.enabled` | string list | all installed | install | Operator | No; audited |
| `wire.codecs.preference` | string list | `json` first until the spike ranks them | install | Operator | No; audited |
| `wire.codecs.preference.by_device_class` | map | empty | install | Operator | No; audited |
| `wire.discovery.enabled` | bool | true | install | Operator | No; audited |
| `wire.transport.race_stagger_ms` | int | 250 | install | Operator | No; audited |
| `wire.quic.stream_per_subscription` | bool | false | install | Operator | No; audited |
| `wire.decode.max_depth`, `wire.decode.max_string_bytes`, `wire.decode.max_collection_items` | int | 32, 1048576, 100000 | install | Operator | Raising above default: yes |
| `wire.client.transport_order` | string list | `quic, h3-grpc, webtransport, grpc, ws` | device | User | No |

The default codec preference moves to the benchmark winner for .NET to .NET connections only when the spike passes (section 9) and the owner approves the new default in a card; until then JSON stays first.

### 9. Transport specifics [PROPOSED]
- **`ws`:** unchanged. Credential in the upgrade `Authorization` header or the first `hello`. Close codes as doc 02.
- **`quic`:** one bidirectional control stream carries frames, each prefixed by a QUIC variable-length integer. Credential in the first frame after the preface (`hello`) over a TLS 1.3 connection with ALPN `bazaar-wire/1`; the client pins the server certificate as Conduit offers (doc 39). Optional `stream_per_subscription`: each `gc:<id>` subscription gets its own server-opened unidirectional stream, which removes head-of-line blocking between GCs; order inside a stream is unchanged and cursors are per stream already, so correctness does not depend on cross-stream order. Connection migration helps phones changing networks; resume with cursors stays the correctness mechanism. QUIC keep-alive is set to half `wire.heartbeat_interval_s`; wire `ping` stays as the application-level heartbeat. Close codes map to QUIC application error codes with the same numbers.
  - *Implementation (`Bazaar.Server.Transport.QuicNative`, `Bazaar.Client.Transport.QuicNative`):* the preface is `0xBA 0x5A <len:1 byte> <ASCII codec names, comma separated>`; the client sends its codecs in preference order and the server answers with a preface holding the one name it chose (the first client name the operator enabled; none in common closes with 4400). The server accepts exactly one inbound bidirectional stream and no unidirectional ones, with no 0-RTT. The first frame after the preface is capped at 8 KiB and the whole connect-to-`hello` span at `wire.hello_timeout_s` (4400 for a bad preface or oversized frame, 4408 on timeout, 4429 when too many handshakes are pending). `MaxFrameBytes` is checked on the declared length before any payload byte is buffered. The shared stream logic (`WirePreface`, `WirePrefaceHandshake`, `StreamWireConnection`) lives in `Bazaar.Wire.Abstractions` so `webtransport` can reuse it. The server module exposes its `Offers` and hands finished connections to the connection layer through `IWireConnectionSource.Accepted` (a channel reader). The client exposes a certificate pinning hook (`QuicWireConnectorOptions.PinCertificate`, helper `QuicCertificatePin.Sha256`). `stream_per_subscription` is a type-level option only: setting it makes the listener throw `NotSupportedException` at start (TODO, not built).
- **`webtransport`:** HTTP/3 extended CONNECT on `/wt`, credential in the CONNECT request headers, one bidirectional stream with the same framing as `quic`. Datagrams are not used for anything that must arrive. The ASP.NET Core WebTransport feature has been preview-gated; the module ships only when the .NET 11 server API is stable, else it is marked Experimental and off by default (spike S-WTX decides).
- **`grpc`:** one service `bazaar.wire.v1.Session` with `rpc Connect(stream Frame) returns (stream Frame)`; `Frame { bytes data }` holds one codec frame, so any codec works and `protobuf` is the native pairing. Credential in `authorization` metadata, checked by the same `IConnectionAuthenticator`. Close codes travel in trailers. **`grpc-web`** cannot do client streaming, so it uses `Subscribe(SubscribeRequest) returns (stream Frame)` plus unary `Send(Frame) returns (Frame)`, both mapped onto the same connection manager.
- **Platform support:** `System.Net.Quic` needs MsQuic (bundled with Windows 11 and Server 2022 or newer; the `libmsquic` package on Linux) and reports `QuicConnection.IsSupported`; the modules check it and drop out of negotiation cleanly. Android has no MsQuic: its ladder is `AndroidHttp3` (Cronet, HTTP/3) then `grpc` over HTTP/2 then `ws`. Other platforms without MsQuic fall back to `grpc` over HTTP/2 or `ws`. Cronet offers HTTP/3 streams, not raw QUIC, which is why the Android module carries gRPC framing; whether Cronet's bidirectional stream keeps long-lived streams alive through Doze and network handover is measured in the spike. Support per platform, and whether each client head can load each module, is measured in the spike, not assumed.
- **Server runtime:** all transports feed the existing connection manager through `IWireConnection`. Nothing about GC sequencing, subscription, replay or the idempotent request layer changes.

Real-network tests (QUIC, later WebTransport and gRPC over HTTP/3) run in `.github/workflows/wire-real-net.yml` on the owner's self-hosted Windows runners only (label `bazaar-win`, which ships MsQuic), with `BAZAAR_TEST_REAL_NET=1`. The job has no hosted fallback, is skipped for forks and any actor but the owner, and is not a required check.

### 10. Benchmark spike S-WTX [PROPOSED]
Pass criteria, fixed before the first run. **Correctness is mandatory for every module** (conformance suite green; foreign-client vectors round-trip; fuzz clean). **Performance decides only the default preference order and which candidates join the wave, not whether the five named transports and four named codecs ship** (owner asked for all of them).
- Corpus: the S6 corpus (10 000 synthetic messages, 200-message page, 10 000-frame replay, fixed seed) plus a Constellation snapshot payload.
- Codecs: json, memorypack, cbor, msgpack, protobuf, plus any candidate. Measure bytes (raw, gzip, brotli), encode and decode time, allocations per frame, AOT publish size and warnings, on Windows (owner PC), Linux (cloud) and Android (device).
- Transports: ws/h1, ws/h2, ws/h3, quic, webtransport, grpc (h2 and h3), grpc-web, and on Android the Cronet HTTP/3 module against `grpc` over HTTP/2. Measure connect time (cold and resumed), time to first replay page, throughput at 1 000 msg/s, CPU per 1 000 msg/s, head-of-line behaviour under 1% and 5% loss with 50 ms RTT (`tc netem` on Linux, a loss shim on Windows), and reconnect after a network change.
- Report against today's JSON over ws as the baseline; record the owner PC and device models; three process runs per row.

## Work packages (build order)
Docs PR first (this doc plus edits to 02, 39, DECISIONS-LOG, S6, CLAUDE.md, README). Then:
0. Spike S-WTX (parallel with 1).
1. `Bazaar.Wire.Abstractions`, `Bazaar.Wire.Conformance`, the generator and `[WirePayload]` markers on the existing records, `schema.lock.json` gate, architecture tests. Moves `IWireTransportFactory`, `IWireConnection`, `WireReceive` out of Client.Core. Depends on the Wire extraction PR (#839) landing.
2. Codec modules (four in parallel): `Cbor`, `MessagePack`, `MemoryPack`, `Protobuf`.
3. Move `ws` out of Server.Wire and Client.Core into `Transport.WebSocket` (pure move; existing tests pass untouched). Negotiation (Mode A subprotocol, Offers, race, negative cache) in the connection layer.
4. Transport modules (parallel): `Http3`, `QuicNative`, `Grpc` (+ `grpc-web`), `WebTransport`.
5. `Server.Wire.Discovery`, schema publication, golden vectors, admin pane entries (doc 51), settings registration.
6. Transforms and spike candidates, only if S-WTX recommends.

## Tests
Conformance suite per codec and per transport (envelope round trip for every `[WirePayload]`, 2000 seeded mutated and truncated frames that may only fail with `WireFormatException`, unknown-field tolerance, unknown message type, truncated and oversized frames, depth and length caps, cursor resume after a dropped connection, idempotent retry across a reconnect). Negotiation tests (Mode A, Mode B, old client, downgrade hash mismatch, operator disables a codec mid-session: existing connections stay, new ones renegotiate). Fuzz per binary codec. Headless UI tests for the settings pane. Real-network tests (QUIC, WebTransport) gated behind `BAZAAR_TEST_REAL_NET=1`, fakes otherwise, so CI on any runner stays green.

## Conflicts
- `CLAUDE.md` and DECISIONS-LOG round 15 say "no Protobuf tooling": narrowed to "outside the gRPC and Protobuf codec modules" by this doc.
- Doc 02 says binary Protobuf frames are not built and the socket is the only channel: updated to point here; the connection is still one authenticated channel per device, only its carrier varies.
- Doc 39 leaves the QUIC framing for `p2p` open: settled by `quic` in section 9.
- `Bazaar.Contracts` holds `JsonWireCodec` today. Kept; moving it is a doc 40 matter.

## Questions for the owner
1. ANSWERED (owner card, 2026-11-19, extended by the owner the same day): Android has no MsQuic in .NET, so `grpc` over HTTP/2 is the Android floor. The owner also wants QUIC on Android, so the `AndroidHttp3` module (Cronet) is tried first and falls back to HTTP/2. [DECIDED]
2. ANSWERED (owner card, 2026-11-19): the benchmark winner becomes the default codec for .NET to .NET connections after the spike, with JSON staying the floor and the owner approving the final default. [DECIDED]
3. ANSWERED (owner card, 2026-11-19): `webtransport` and `grpc-web` are built now and ship off by default; an operator turns them on. [DECIDED]
4. ANSWERED (owner card, 2026-11-19): the spike evaluates all five (FlatBuffers, Avro, Bebop, Cap'n Proto, Arrow IPC); each joins the build only if it passes the conformance suite and stays AOT-clean. [DECIDED]
