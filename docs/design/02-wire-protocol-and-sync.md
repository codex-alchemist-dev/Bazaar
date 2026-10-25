# Wire Protocol and Sync

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 4
Depends on: 01-gc-data-model.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 32-remote-access-auth-and-privacy.md · Used by: 12-kanban-and-dependencies.md, 18-vault.md, 19-constellation-and-git-solar-system.md, 20-engine-integration-api.md, 21-github-integration.md, 22-email-subsystem.md, 23-observatory-and-usage.md, 26-fun-systems.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 33-backup-dr-and-export.md, Bazaar.Sdk

## Purpose
Defines how clients talk to Bazaar.Server: one raw WebSocket per device, plus an authenticated HTTP surface for blobs, search, pairing and status. The server decides layout, unread state and what is shown; clients render.

## Decided inputs
- Raw WebSocket; Socket.io dropped (Bazaar.Server.md §3).
- Wire format [DECIDED 2026-10-25, owner, spike S6, DECISIONS-LOG round 15]: JSON via shared C# contracts with source-generated System.Text.Json. A non-.NET client is a fallback only (Rust). No Protobuf tooling.
- WebSocket for realtime; authenticated HTTP for blobs, search and pairing (round 6, C15).
- GC log: `gc_event`, `gc_message`, `gc_message_version` (full copy per version). Append-only, never purged, edits keep every version (round 1, C09).
- Per-GC gap-free sequence; hash partitions by `gc_id` from day 1 (round 1, C10). Append and `NOTIFY` in one transaction (doc 04).
- GC fan-out uses Postgres NOTIFY; presence deltas may come from the cache provider (Valkey on Linux, Garnet on Windows) (round 1, C07).
- Server does all compute; clients render. Layout stays server-side; clients switch posture variants locally (round 4, C17; round 5, C27).
- Push is a bare wake; content is fetched over the authenticated connection (round 5, C41).
- This doc owns the wire and status format and the protocol version scheme; doc 03 owns live/ready probes; doc 23 owns status contents (round 12, C40).
- Shared rate and quota defaults come from doc 15's table (round 10, C36). No inbound mail or webhook connections by default (round 8, C30).
- No web client; the narrow sign-in page is doc 32's (round 11, C42). Auth design belongs to docs 14 and 32.

## Design

**Connection and handshake [PROPOSED].** `wss://<operator host>/ws`, one socket per device; windows share it (Q3). Authenticate before any other traffic: credential in the upgrade `Authorization` header, or in the first `hello` within 5 s, else close. `hello` carries the version range, codecs, device id, device class, client build and optional cursors. The server runs `IConnectionAuthenticator` (failure: close 4401) and replies `welcome` with the chosen version and codec, principal, server time, heartbeat and limits, capability flags and UI revisions.

**Envelope.** Every frame is one envelope, with fixed fields so JSON and Protobuf carry the same shape. The payload is opaque to the transport.

| Field | Meaning |
|---|---|
| `v` | protocol major version |
| `t` | message type, e.g. `gc.message` |
| `k` | kind: `req`, `res`, `evt`, `err` |
| `id` | client-chosen id on `req` (also the idempotency key) |
| `re` | on `res`/`err`: the `id` answered |
| `s` | stream name (registry below) |
| `n` | sequence on a GC stream |
| `ts` | server time, UTC |
| `p` | payload |

Requests get exactly one `res` or `err`. A retried request with the same `id` returns the original result (ids kept per device for 24 h), so a send over a flaky link never posts twice. Server-originated changes are events with no reply.

**Sequence numbers.**
- Stream `gc:<id>` uses two gap-free counters on the `gc` row, both assigned inside the write transaction with `UPDATE ... RETURNING` (doc 04). Rollbacks leave no gaps.
  - `n`: every `gc_event` row (messages, system and other events).
  - `ev` [PROPOSED, Q1]: every new message version. Edits are versions, not log entries, so an edit takes no `n`. The `gc.message` event for an edit carries the message's `n` and the new version's `ev`.
- A client's per-GC cursor is the pair `(n, ev)`.
- Other streams are state, not history. Each carries a `revision`; clients take a snapshot on subscribe, then deltas, and re-fetch on any doubt.

**Stream registry [PROPOSED].** A stream is state unless listed here as a log.

| Stream | Kind | Owner doc |
|---|---|---|
| `gc:<id>` | log (`n`, `ev`), replayed | 01, 04 |
| `board:<id>` | state, `revision`; snapshot plus revision resync (doc 12 calls it "the board stream") | 12 |
| `presence` | state; deltas via cache provider | 23 |
| `ui` | state, `ui.changed` | 27 |
| `graph:<id>` | state; carries `GraphSnapshot` and `LayoutFrame` entries (each with `GraphVersion`). `FocusRequest` is a client command, not a stream entry | 19 |
| `pane` | state (server-streamed agent terminal panes) | 20 |
| `approvals` | state (pending owner and Tier 2 decisions) | 15 |

**Subscriptions.** `subscribe { streams: [{ s, after, editAfter }] }`. The server checks access per stream (membership; DM rules: owner reads all, others by granted access) and replies `ok`, `denied` or `resync` per stream. Access is rechecked on subscribe and on membership change (`stream.revoked`).

**Replay after reconnect [PROPOSED].**
1. The client sends `after` (last `n` applied) and `editAfter` (last `ev` applied).
2. The server buffers live events, replays Postgres pages (default 200): entries with `n > after`, and the current version of each message with `ev > editAfter`. Older versions come through `gc.history`. It splices to live once contiguous. Clients drop anything already applied.
3. A gap over the cutoff (`wire.replay_cutoff_entries`, default 2,000 entries and versions) gets `resync { latestN, latestEv, unread, tail }`: the last 100 entries plus unread counts. The client pages back with `gc.history { before, limit }` and marks a hole.

**Heartbeats.** Server ping every 20 s; any frame counts as liveness; 60 s of silence closes the socket. Clients send app-level `ping` (the .NET `ClientWebSocket` behaviour on Android is unverified) and reconnect with jittered backoff from 1 s to `wire.reconnect_backoff_max_s` (60 s).

**Backpressure [PROPOSED].** Each connection has a bounded outbound queue (default 1,000 events). On overflow the server stops live delivery for the slow streams and sends `lagged { s, lastSent }`; the client resubscribes from its cursor, so nothing is lost. Inbound: 256 KiB max frame (attachments never travel on the socket), a request rate limit, 32 in-flight requests, `err rate_limited` with `retryAfter`. Compression is off until measured.

**Versioning [PROPOSED].** Version is `major.minor`. Minors only add optional fields, event types and capability flags; receivers ignore unknown fields and events; an unknown request returns `err unsupported`. The server supports the current and previous major. With no overlap, `welcome` carries `upgrade_required` and the minimum build, then the socket closes with 4426.

**Server-driven layout [PROPOSED].**
- `ui.get { deviceClass, viewport, posture }` returns a `UiProfile`: start-screen widgets, navigation, palette commands, theme reference, overrides and preferences. It carries a variant for every posture the server supports (folded, unfolded, half-open), so the client switches locally with no round trip (C27).
- `ui.changed { revision }` follows any change; `prefs.set` is validated and stored by the server.
- Unknown widget types render as a placeholder.
- The client caches the last profile with its revision, plus a minimal built-in fallback shell owned by doc 27, for an offline cold start. The cache never holds GC content. Theme files are served by revision; doc 27 owns the format.

**Authenticated HTTP surface [PROPOSED].** Same TLS and credential as the socket, on a separate listener so its limits do not share a bucket with `/ws`.

| Route | Purpose | Semantics owned by |
|---|---|---|
| `POST /blobs` | Chunked, resumable upload; returns content hash; size limit per doc 01 | 01 |
| `GET /blobs/{hash}` | Range-capable download; a non-viewer gets "not found" | 01 |
| `GET /vault/search` | Paged search over Vault blocks, filtered to the viewer | 18 |
| `POST /pair/offers`, `POST /pair/token` | Pairing | 32 |
| Sign-in and handoff routes | Three routes, off by default, no content | 32 |
| `POST /hooks/github/{opaque}` | Optional webhook, only behind an operator tunnel | 21 |
| `GET /status` | Status page: format here, contents in 23 | 23 |

**Status endpoint [PROPOSED].** `GET /status` returns JSON with a `schema` integer, server and protocol versions, and doc 23's sections, plus a minimal HTML view of the same JSON. It never includes GC, Vault or agent names. Probes are not served here: doc 03 owns `/health/live` and `/health/ready`; there is no `/healthz`. Bind address and token requirement are settings (`wire.status_bind_address`, `wire.status_require_token`; table below).

**Android [PROPOSED].** The socket is held only while the app is foreground or unfolded. Otherwise:
1. The server sees a wake-worthy event (`mention`, `dm`, `owner_decision`; `quota` if opted in) for a device without a live socket and calls `IPushNotifier.WakeAsync`, coalesced to one per device per `wire.push_wake_coalesce_s` (30 s).
2. The app opens a short-lived socket, runs `hello` and `subscribe` with its cursors, builds the local notification and closes.
3. Tokens are registered with `device.registerPush`; provider adapters belong to doc 29.

Late or repeated wakes are harmless because the cursor makes the fetch idempotent. A `dataSync` service is never the notification path.

## Data and interfaces

```csharp
record Envelope(int V, string T, EnvelopeKind K, string? Id, string? Re, string? S, long? N, DateTimeOffset? Ts, ReadOnlyMemory<byte> P); // Ts absent on client frames
interface IWireCodec { string Name { get; } bool IsBinary { get; }
  void Encode(in Envelope e, IBufferWriter<byte> w); Envelope Decode(ReadOnlySpan<byte> frame);   // Decode throws WireFormatException (close 4400)
  ReadOnlyMemory<byte> EncodePayload<T>(T payload); T DecodePayload<T>(in Envelope e); }
interface IConnectionAuthenticator { ValueTask<AuthResult> AuthenticateAsync(HelloCredential cred, DeviceInfo device, CancellationToken ct); }
record AuthResult(bool Ok, PrincipalRef? Principal, string? DeviceId, DateTimeOffset? ExpiresAt, string? Reason);
interface IPushNotifier { Task WakeAsync(string deviceId, WakeHint hint, CancellationToken ct); }
record WakeHint(string Kind); // kind only: no gc id, no seq, no text (C41)
```

Codec is chosen in `hello`/`welcome`: text frames JSON; binary Protobuf frames are not built (S6 chose JSON; a binary codec could be added later behind `IWireCodec`). Optional server bookkeeping: `device_cursor(device_id, stream, applied_n, applied_ev)`; the client's cursor is authoritative for replay.

Close codes: 4400 bad frame, 4401 auth failed or expired, 4403 revoked, 4408 hello timeout, 4426 upgrade required, 4429 rate limited.

Message types (v1 sketch): `hello`, `welcome`, `subscribe`, `unsubscribe`, `gc.message`, `gc.send`, `gc.history`, `gc.read`, `resync`, `lagged`, `stream.revoked`, `presence.update`, `ui.get`, `ui.changed`, `prefs.set`, `auth.refresh`, `device.registerPush`, `ping`/`pong`.

### Settings [PROPOSED, DECISIONS-LOG round 13]
Registered in doc 03's settings registry. Operator-set, install scope. Values live in Postgres; the defaults ship in this table. Every change is audited (doc 15 `audit_log`). Settings may tighten the wire floors and never loosen them below the shipped defaults.

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `wire.replay_cutoff_entries` | int | 2000 | install | Operator | No; audited |
| `wire.replay_page_size` | int | 200 | install | Operator | No; audited |
| `wire.heartbeat_interval_s` | int | 20 | install | Operator | No; audited |
| `wire.heartbeat_timeout_s` | int | 60 | install | Operator | No; audited |
| `wire.reconnect_backoff_max_s` | int | 60 | install | Operator | No; audited |
| `wire.hello_timeout_s` | int | 5 | install | Operator | No; audited |
| `wire.max_sockets_per_device` | int | 5 | install | Operator | No; audited |
| `wire.outbound_queue_max` | int | 1000 | install | Operator | No; audited |
| `wire.inflight_requests_max` | int | 32 | install | Operator | No; audited |
| `wire.max_frame_kib` | int | 256 | install | Operator | No; audited |
| `wire.request_id_retention_h` | int | 24 | install | Operator | No; audited |
| `wire.push_wake_coalesce_s` | int | 30 | install | Operator | No; audited |
| `wire.compression` | bool | false | install | Operator | No; audited |
| `wire.previous_major_supported` | bool | true | install | Operator | No; audited (false sends N-1 clients `upgrade_required`) |
| `wire.allow_plaintext_overlay` | bool | false | install | Operator | Yes (owner); audited. Plaintext is loopback-only by default |
| `wire.status_bind_address` | string | `127.0.0.1` | install | Operator | Yes (owner); audited |
| `wire.status_require_token` | bool | true | install | Operator | Yes (owner); audited. Non-loopback binds without a token are refused |

Needs counsel: none. No row changes legal exposure; the status page never carries GC, Vault or agent names.

## Behaviour and edge cases
- Ordering: per stream, in commit order (entries by `n`, versions by `ev`). None across streams.
- Dual path: GC events come from an in-process dispatcher fed by a `LISTEN` connection; presence comes from the cache provider behind one interface (doc 03). A lost NOTIFY is recovered by re-reading the log by `n` and `ev`: it delays, never drops.
- `err stale_cursor` if `after` or `editAfter` exceeds the server's latest (restored database); the client resyncs that stream.
- Restore [PROPOSED, doc 33]: counters move above any value a surviving client may hold, and one `system` recovery entry is written. This is a recorded exception to gap-free sequencing (docs 01 and 04 list it).
- Sends: the response carries the assigned `n`; the echo arrives on the stream and is de-duplicated by `id`.
- Edits: clients render the current version with an "edited" marker; history is `gc.history`.
- Limits are the settings above: 5 sockets per device identity (newest wins), 1,000 queued events, request ids kept 24 h. Shared rate buckets come from doc 15.
- Security: reject plaintext credentials unless loopback, or an overlay the operator allowed (`wire.allow_plaintext_overlay`); never log payloads; `err` leaks no membership (denied and missing look the same).
- Clients never use local time for ordering.

## Testing
Real Postgres; in-process Kestrel with `ClientWebSocket`; fake clock, authenticator and push notifier. No models, no display.
- Replay: 10k messages offline, reconnect, exact set and order, no duplicates; kill the socket mid-page and resume.
- Splice: appends during replay give no gap or duplicate (property test).
- Edits: an offline edit replays once as the current version; `n` does not advance.
- Cutoff: a gap over 2,000 yields `resync`; `history` fills the hole. Restore: a cursor above the restored head gets `stale_cursor`.
- Idempotency, slow consumer (`lagged`, no loss), versioning (unknown fields, no-overlap 4426).
- Auth: revocation closes within 1 s; DM denial looks like missing. Heartbeat: silent peer closed at 60 s.
- HTTP: chunked upload resumes after a dropped chunk. UI: unknown widget renders the placeholder (Avalonia.Headless). Status: shape snapshot, no chat text.

**Wire-format spike S6 [DECIDED: JSON; part 1 done, record in `docs/spikes/S6.md`].** Original plan, criteria in priority order: non-.NET consumers; toolchain cost on all heads including Android; AOT/trim safety on Android; wire debuggability; schema evolution; size and CPU. One throwaway branch, both codecs behind `IWireCodec`: implement `hello`, `subscribe`, `gc.message`, `lagged`; run the cases above; measure bytes and time for a 200-message page and a 10k replay on the owner's PC and the Android foldable; add a field and an unknown message; check Android trim/AOT warnings; record LOC and build steps. Decision rule [PROPOSED]: JSON unless a non-.NET consumer is a v1 requirement or measurements show a problem JSON cannot fix. Android items (background socket, Doze wake, network switch, overlay path) belong to doc 29.

## Work packages
1. `Bazaar.Contracts`: envelope, kinds, codes, message records (`gc.message` with `n` and `ev`), JSON source-gen context.
2. `IWireCodec` + JSON codec + golden-file tests (WP1).
3. GC sequencing: `n` and `ev` counters in append and edit transactions (doc 04; independent of WP1).
4. Connection manager: upgrade, hello/welcome, authenticator hook, heartbeats, limits (WP1, WP2).
5. Subscription engine: access, `(n, ev)` replay paging, splice, `resync`, `history` (WP3, WP4).
6. Dispatcher: `LISTEN` consumer, presence source, bounded queue, `lagged` (WP4).
7. Idempotent request layer (WP4).
8. Client transport in `Bazaar.Client.Core`: reconnect, `(n, ev)` cursor store, de-dup, ping (WP1, WP2).
9. `ui.get`, `ui.changed`, `prefs.set` with posture variants; placeholders and fallback shell with doc 27 (WP4).
10. HTTP surface: chunked blobs, search and pairing routes, separate rate bucket (WP4; semantics in docs 01, 18, 32).
11. Status endpoint format and version reporting (independent).
12. `IPushNotifier`, wake coalescing, `device.registerPush` (WP4; doc 29).
13. Protobuf codec, spike branch only (WP2).
14. Test harness: Kestrel, Postgres fixture, fake clock (WP2).

## Conflicts
- Bazaar.Server.md §3 transport cell: fixed with the S6 decision (DECISIONS-LOG round 15).
- Garnet on Windows is unverified (spike S2); presence deltas wait on it.
- The Android `dataSync` 6 h cap is disputed; either way no always-on socket is planned.
- Cloudflare Tunnel WebSocket fit is unverified; if an operator uses it, long sockets and heartbeats need testing.
- Doc 01 still models one `entry` table with edits as entries and numbers edits in Q1; it must follow C09 and Q1 here.
- Doc 04 must add the `ev` counter and `edit_seq` (if Q1 is accepted) and list the restore exception.
- Doc 30 says the protocol version is an integer and keeps a `dataSync` keep-alive; both must change (C40, C41).
- Resolved: doc 03 defines no `/healthz`; it owns `/health/live` and `/health/ready`.

## Questions for the owner
Answered and moved out: sequence per GC (C10), cached profile and fallback shell (C27), bare wake (C41), wake kinds (C41). Plain values and policy choices are now settings (table above).

1. Do edits take a sequence number? Recommend a separate per-GC edit counter `ev`: C09 keeps edits out of the log, and this keeps replay complete. Alternative: edits take `n`, as doc 01's question defaults. (a) separate `ev` (rec); (b) edits take `n`. This is a wire and schema shape, so it needs one answer.
2. Wire format **[ANSWERED 2026-10-25, owner: JSON; non-.NET is a fallback only, Rust]**: adopt "JSON unless a non-.NET consumer is a v1 requirement"? Recommend yes. Is a non-.NET SDK or plugin planned for v1? This is a wire-format and scope question.
3. One socket per device, with windows sharing it? Recommend yes. This changes the connection model, so it stays a question.
4. Confirm the restore policy: sequence floor plus a `system` recovery entry (PROPOSED in doc 33; FOLLOW-UP in DECISIONS-LOG). Recommend confirm. It changes gap-free sequencing, so it stays a question.

MOVED TO SETTINGS: wire.replay_cutoff_entries, wire.replay_page_size, wire.heartbeat_interval_s, wire.heartbeat_timeout_s, wire.reconnect_backoff_max_s, wire.hello_timeout_s, wire.max_sockets_per_device, wire.outbound_queue_max, wire.inflight_requests_max, wire.max_frame_kib, wire.request_id_retention_h, wire.push_wake_coalesce_s, wire.compression, wire.previous_major_supported, wire.allow_plaintext_overlay, wire.status_bind_address, wire.status_require_token (DECISIONS-LOG round 13)
