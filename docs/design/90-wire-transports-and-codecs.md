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
| `Bazaar.Wire.Generators` | Roslyn incremental generator and analyzer (build-time only; netstandard2.0, ships as an analyzer) | Reads `[WirePayload]` records in `Bazaar.Contracts`; emits the type registry and per-codec adapters. Built for `cbor`; schema artifacts (section 5), the `schema.lock.json` gate and the other codecs are still to do. A codec project opts in with `[assembly: GenerateWireCodec("cbor")]`. |
| `Bazaar.Wire.Conformance` | Test library | One conformance suite per transport and per codec, reused by every module's tests. The codec half is built: a test project inherits `WireCodecConformance` and supplies the codec plus a hook that adds an unknown field; it runs against `JsonWireCodec` and `CborWireCodec`. The transport half is still to do. |
| JSON codec | Stays in `Bazaar.Contracts` as `JsonWireCodec` for now | The floor. Moves to `Bazaar.Wire.Codec.Json` only if doc 40 asks; no behaviour change. |
| `Bazaar.Wire.Codec.MemoryPack` | `memorypack` | .NET to .NET only. Fastest expected. |
| `Bazaar.Wire.Codec.Cbor` | `cbor` | `System.Formats.Cbor` (a NuGet package from the .NET team, 11.0.0; it is not in the shared framework, so the module takes one package dependency). Self-describing, standard, good for outside clients. Built (WP2); shape in section 3.1. |
| `Bazaar.Wire.Codec.MessagePack` | `msgpack` | Generated formatters only; no typeless mode. |
| `Bazaar.Wire.Codec.Protobuf` | `protobuf` | Generated `wire.proto`; used by gRPC and available to any transport. |
| `Bazaar.Server.Transport.WebSocket` / `Bazaar.Client.Transport.WebSocket` | `ws` | The existing WebSocket moved out of `Bazaar.Server.Wire` (`UseBazaarWire`) and `Bazaar.Client.Core/Wire/WebSocketWireTransport.cs`. Behaviour identical. HTTP/1.1, HTTP/2 (RFC 8441) and, when `Http3` is on, HTTP/3 (RFC 9220). |
| `Bazaar.Server.Transport.Http3` | `h3` listener feature | Adds HTTP/3 (QUIC) bindings and `Alt-Svc` to the existing listeners so `ws`, `grpc` and `webtransport` are reachable over HTTP/3. Not a transport itself. |
| `Bazaar.Server.Transport.QuicNative` / `Bazaar.Client.Transport.QuicNative` | `quic` | Raw `System.Net.Quic`, ALPN `bazaar-wire/1`, no HTTP. Settles the QUIC framing question that doc 39 left open for `p2p`. |
| `Bazaar.Client.Transport.AndroidHttp3` | `grpc` over HTTP/3 on Android | Owner, 2026-11-19. Client-only module (Android head). Uses Cronet (Chromium network stack, QUIC and HTTP/3 built in, no MsQuic needed) through its bidirectional stream API, and speaks the same gRPC framing as `grpc` to the server's existing `grpc` endpoint, so the server needs only the `Grpc` and `Http3` modules. Tried first on Android; any failure falls back to `grpc` over HTTP/2, then `ws`. |
| `Bazaar.Server.Transport.WebTransport` (+ `Bazaar.Client.Transport.WebTransport` if the spike allows) | `webtransport` | For browsers and other outside clients. A .NET client uses `quic`. |
| `Bazaar.Server.Transport.Grpc` / `Bazaar.Client.Transport.Grpc` | `grpc`, `grpc-web` | `Grpc.AspNetCore`, `Grpc.Net.Client`, `Grpc.AspNetCore.Web`; `Grpc.Tools` is allowed in these two modules only. Built (WP4); shape in section 9. Pairs natively with the Protobuf codec but references no codec: `Frame` carries opaque codec bytes. |
| `Bazaar.Server.Wire.Discovery` | `/.well-known/bazaar/wire` and the schema routes | Read-only, unauthenticated, allowed on the status surface (section 6). |
| `Bazaar.Wire.Transform.Zstd` (later) | `zstd` | Section 7. |

Candidates evaluated in the spike and built only if they pass the conformance suite and stay AOT-clean: FlatBuffers, Apache Avro, Bebop, Cap'n Proto, Apache Arrow IPC (for bulk table-shaped payloads such as Constellation snapshots), Brotli and Zstd as transforms. Each would be one more module with no change to anything else.

Dependency rules, enforced by `tests/Bazaar.Architecture.Tests`: a transport or codec module references only `Bazaar.Wire.Abstractions`, `Bazaar.Contracts` and its own third-party packages; no module references `Bazaar.Server.csproj`; transports never reference codecs (the connection layer joins them); no module has a hard edge to a codec (the gRPC module pairs with `protobuf` by convention only: its `Frame` carries any codec's bytes). A missing optional module only removes its own entries from negotiation.

### 3. Contracts: one source, generated for every codec [PROPOSED]
`Bazaar.Contracts` stays plain records (S6 rule: camelCase names, explicit nullables, no polymorphic or tuple payloads, no codec attributes, no codec packages). Two additions, both dependency-free:

1. `[WirePayload("gc.message")]` marks a record as a wire payload and gives its message type. This replaces the hand-kept type lists. The attribute allows several uses on one record (a record shared by two message types, such as the pause and resume requests) and has a parameterless form for a reply or a shape shared between messages that has no message type of its own. Records that are only nested inside a payload need no marker; the generator follows property types. The `JsonSerializable` list in `WireJson.cs` stays until the JSON context is itself generated.
2. Field identity for schema-first codecs: positional order is the field number (1-based), append-only. The analyzer fails the build on reordering, removal or a type change of a published field, and on a new field that is not nullable or defaulted. A checked-in `docs/wire/schema.lock.json` is regenerated by the generator and diffed in CI; a breaking diff needs a protocol major bump (doc 02 versioning).

`Bazaar.Wire.Generators` emits per codec module, from the same records:
- JSON: the existing `System.Text.Json` source-generated context.
- CBOR: direct `CborWriter`/`CborReader` code, map keys equal to the JSON camelCase names (not short names: one name set for JSON, CBOR and the published schema), no reflection. Section 3.1 gives the shape.
- MessagePack and MemoryPack: mirror DTOs carrying the codec's attributes plus both-way mappers, generated into the codec assembly so Contracts never sees the package. Mirrors use the codec's version-tolerant mode with explicit field order.
- Protobuf: `wire.proto` and mirror messages plus mappers (the S6 mapping layer, now generated instead of hand-written).

Every generated path is AOT and trim clean (`IsAotCompatible`, warnings as errors). The Protobuf runtime's IL3050 and IL3053 warnings from S6 get one documented suppression inside the Protobuf codec module only.

Forward and backward rules, identical for every codec and pinned by the conformance suite: unknown envelope and payload fields are ignored; an unknown message type leaves `p` opaque so the dispatcher answers `err unsupported` without decoding it; a field absent from an older sender reads as null or default; a decoder never throws on unknown enum values when the enum has a fallback member: it maps to the member named `Unknown`, or `Unspecified` when there is no `Unknown`. An enum with neither (such as `PrincipalKind`) throws `WireFormatException` for a value it does not know, because guessing an identity kind would be worse than refusing the frame.

### 3.1 CBOR shape [DECIDED in code, WP2]
- Envelope: one definite-length map with the doc 02 field names `v`, `t`, `k`, `id`, `re`, `s`, `n`, `ts`, `p`; absent optional fields are omitted. `k` is the text `req`, `res`, `evt` or `err`; `ts` is a tag 0 date-time string (UTC).
- `p` is the payload's own CBOR data item embedded in the envelope map, not a byte string wrapped around it. A decoder finds its extent with a depth-limited skip and keeps the bytes unparsed until `DecodePayload`, so an unknown message type costs no payload decoding and reaches the dispatcher byte for byte.
- Payload: a map with text keys equal to the JSON property names; null fields are omitted (same as JSON). `Guid` is the text form `D`, `DateTimeOffset` a tag 0 string, `decimal` a tag 4 decimal fraction, integers are CBOR integers. `float[]` and `ReadOnlyMemory<float>` are one RFC 8746 typed array: tag 85 (little-endian IEEE 754 binary32) over a byte string. Enums are text: the `JsonStringEnumMemberName` value when present, else the camelCase member name (this differs from JSON, which writes the numeric value for enums without a converter; text is self-describing and lets an unknown value fall back, see section 3).
- Reader and writer use `CborConformanceMode.Strict`: duplicate map keys, non-text keys, invalid UTF-8 and trailing bytes after the item are `WireFormatException`.
- A missing field reads as default for value types, null for nullable types, and the constructor default when the record declares one; a missing non-nullable reference field (such as `text`) is a `WireFormatException`.
- Every failure of a decoder, including a record constructor refusing values, surfaces as `WireFormatException`; an unregistered `T` is `NotSupportedException`.

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
- **`webtransport`:** HTTP/3 extended CONNECT on `/wt`, credential in the CONNECT request headers, one bidirectional stream with the same framing as `quic`. Datagrams are not used for anything that must arrive. The ASP.NET Core WebTransport feature has been preview-gated; the module ships only when the .NET 11 server API is stable, else it is marked Experimental and off by default (spike S-WTX decides).
- **`grpc`:** one service `bazaar.wire.v1.Session` with `rpc Connect(stream Frame) returns (stream Frame)`; `Frame { bytes data }` holds one codec frame, so any codec works and `protobuf` is the native pairing. Credential in `authorization` metadata, checked by the same `IConnectionAuthenticator`. Close codes travel in trailers. [DECIDED in code, WP4] The hand-written `session.proto` lives in both modules (different `csharp_namespace`, same wire contract). The server checks the credential through `IWireTransportAuthenticator` (`Bazaar.Wire.Abstractions`; the connection layer adapts `IConnectionAuthenticator` to it) and hands each accepted call to `IWireConnectionHandoff` as an `IWireConnection` (`ICloseableWireConnection` lets the connection layer close with a code). Codec per section 4.2 Mode A: `bazaar-codec` request metadata (comma list, preferred first), else a `content-type` suffix (`application/grpc+cbor`), else the floor codec; the choice is echoed in the `bazaar-codec` response header, and no common codec is refused with 4400. A missing or refused credential is `Unauthenticated` with close code 4401. The close code travels as the trailer `bazaar-close-code`. `MaxFrameBytes` is the gRPC send and receive message limit (plus 16 bytes of field header) and is checked again on the frame length. The client connector (`GrpcWireConnector`) takes its credential from a provider in `GrpcConnectorOptions`, speaks HTTP/2 and asks for HTTP/3 (falling back) when `PreferHttp3` is set. **`grpc-web`** (off unless `wire.transports.enabled` names it; `WireTransportsEnabledOptions` holds the rule; the endpoint only switches gRPC-Web translation on when enabled, the host calls `UseBazaarGrpcTransport` before `MapBazaarGrpcTransport`, and a browser needs CORS from the host; `Subscribe` carries a client-chosen session id of 16 to 128 URL-safe characters, and `Send` names it in `bazaar-session` metadata with the same credential)  cannot do client streaming, so it uses `Subscribe(SubscribeRequest) returns (stream Frame)` plus unary `Send(Frame) returns (Frame)`, both mapped onto the same connection manager.
- **Platform support:** `System.Net.Quic` needs MsQuic (bundled with Windows 11 and Server 2022 or newer; the `libmsquic` package on Linux) and reports `QuicConnection.IsSupported`; the modules check it and drop out of negotiation cleanly. Android has no MsQuic: its ladder is `AndroidHttp3` (Cronet, HTTP/3) then `grpc` over HTTP/2 then `ws`. Other platforms without MsQuic fall back to `grpc` over HTTP/2 or `ws`. Cronet offers HTTP/3 streams, not raw QUIC, which is why the Android module carries gRPC framing; whether Cronet's bidirectional stream keeps long-lived streams alive through Doze and network handover is measured in the spike. [DECIDED in code, WP-android-h3] `Bazaar.Client.Transport.AndroidHttp3` is platform-neutral (net11.0): `ICronetEngine` and `ICronetBidirectionalStream` are the seam the Android head fills with the Java Cronet binding. `CronetHttp3Transport` advertises the wire id `grpc`, sends the `bazaar-codec` metadata and the credential, keeps early data off, writes the gRPC 5-byte prefix and the protobuf `Frame` by hand (the same bytes as the `grpc` module, no codegen), and checks `MaxFrameBytes` on the declared length before reading a body (4400 on excess or on a compressed message). Cronet settling on anything but `h3`, no Cronet, a failed open or a non-200 answer throws `WireFallbackException`; `WireConnectLadder` then moves to `grpc` over HTTP/2 and `ws` by `WireNegotiator.OrderOffers`, while a server refusal (4401, 4400) stops the walk. Network handover, suspend and resume, lost streams and each fallback reach an `IWireConnectionObserver` (defined in this module); the module sets no timeout or retry numbers, those come from the spike. Support per platform, and whether each client head can load each module, is measured in the spike, not assumed.
- **Server runtime:** all transports feed the existing connection manager through `IWireConnection`. Nothing about GC sequencing, subscription, replay or the idempotent request layer changes.

### 9a. Connection lifecycle hook [PROPOSED]
Requested by the telemetry thread (crash screenshot uploads hold per-connection state in memory). `Bazaar.Wire.Abstractions` defines `IWireConnectionObserver` with `ValueTask OnClosedAsync(Guid connectionId, CancellationToken cancellationToken)`. The connection layer resolves `IEnumerable<IWireConnectionObserver>` and calls each observer once per connection on any close (clean, error, timeout, revoke), after the connection's in-flight handlers have finished, whatever the transport. An observer that throws or exceeds the 5 s close budget is logged and skipped; it never blocks the close or the other observers. `connectionId` is the id the connection layer assigns, the same on every transport. The hook carries no credential, principal or frame data; an observer that needs those keeps its own map keyed by `connectionId`.

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
