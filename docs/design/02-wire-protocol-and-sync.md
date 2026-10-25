# Wire Protocol and Sync

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: (GC log / data layer doc, number TBD), (auth and pairing doc, TBD) · Used by: (Android push and notifications doc, TBD), client shell docs, Bazaar.Sdk

## Purpose
Defines how every client talks to Bazaar.Server: one raw WebSocket per device, a small envelope, per-GC sequence numbers, replay after any gap, heartbeats, backpressure, versioning, server-driven layout, and the read-only status endpoint. It is the only client-server channel, so clients stay thin and the server decides everything (layout, what is unread, what is shown).

## Decided inputs
- Raw WebSocket, Socket.io dropped; WebTransport revisited at v1.0, not now (Bazaar.Server.md s3).
- Wire format (shared C# contracts + System.Text.Json source generation vs Protobuf) is OPEN until the first client-server spike (CLAUDE.md; Bazaar.Server.md s3). Do not add Protobuf tooling yet.
- Envelope with per-GC sequence numbers and replay-from-seq (implementation-plan.md; Bazaar.Server.md s3, RESEARCHED).
- GC log is append-only and never purged; edits keep every version (CLAUDE.md). Append and `NOTIFY {gc_id, seq}` happen in one transaction (implementation-plan.md).
- Server does all compute; clients render. No web client; a read-only status page is allowed (CLAUDE.md).
- Android: content-free push wake-ups through one provider interface (FCM and UnifiedPush/ntfy), then fetch over the authenticated connection (Bazaar.Android.md).
- One OS-agnostic server build; the cache/pub-sub provider is not required by this protocol.
- Auth design belongs to another doc; this doc only defines the hook.

## Design

**Connection.** `wss://<operator host>/ws`, one socket per device (a device may be a Windows, Android or Linux head, or the SDK). TLS is expected; remote access (Tailscale etc.) is the operator's choice and outside this doc. [PROPOSED] Authenticate before any other traffic: credential in the upgrade `Authorization` header, or, if the client cannot set headers, in the first `hello` frame within 5 s, else close.

**Handshake [PROPOSED].**
1. Client `hello`: protocol version range, codecs it speaks, device id, device class, client build, cursors optional.
2. Server calls `IConnectionAuthenticator` (below); on failure it closes with 4401.
3. Server `welcome`: chosen version and codec, principal ref, server time, heartbeat and limits, server capability flags, UI revision numbers.

**Envelope.** Every frame is one envelope. Fields are fixed so JSON and Protobuf can carry the same shape; the payload is opaque to the transport and decoded by message type.

| Field | Meaning |
|---|---|
| `v` | protocol major version |
| `t` | message type string, e.g. `gc.message` |
| `k` | kind: `req`, `res`, `evt`, `err` |
| `id` | client-chosen id on `req` (also the idempotency key) |
| `re` | on `res`/`err`: the `id` answered |
| `s` | stream name for events: `gc:<id>`, `board:<id>`, `presence`, `ui` |
| `n` | sequence on that stream (see below) |
| `ts` | server time, UTC |
| `p` | payload |

**Request/response vs events.** Requests (`send message`, `subscribe`, `history`, `ui.get`, `device.registerPush`) get exactly one `res` or `err`. Server-originated state changes are events with no reply. A retried request with the same `id` returns the original result (server keeps ids per device for 24 h), so a send over a flaky link never posts twice.

**Sequence numbers.**
- Stream `gc:<id>`: `n` is a gap-free integer per GC, assigned inside the append transaction (per-GC counter updated with `UPDATE ... RETURNING`, which serializes per GC and avoids rollback gaps that a plain Postgres sequence would leave). Edits are new log entries with their own `n` pointing at the original, so replay is just "everything after `n`".
- Streams that are not append-only logs (presence, board state, `ui`) are state, not history: each carries a `revision`; clients get a snapshot on subscribe and then deltas, and on any doubt re-fetch the snapshot. Only GC streams are replayed.

**Subscriptions.** `subscribe { streams: [{ s, after }] }`. The server checks access per stream (membership; DM rules: owner reads all, others by granted access) and replies per stream with `ok`, `denied` or `resync`. `unsubscribe` is symmetric. Authorization is rechecked on each subscribe and when membership changes (server pushes `stream.revoked`).

**Replay after reconnect [PROPOSED].**
1. Client sends its stored cursor per GC (`after = last n it applied`).
2. Server starts buffering live events for that subscription, then replays from Postgres in pages (default 200 entries), then splices to live when `n` is contiguous. No gap and no duplicate is possible because `n` is contiguous; clients still drop anything with `n <= applied` (idempotent).
3. If the gap is larger than a cutoff (default 2,000 entries), the server answers `resync { latestN, unread, tail }`: the last 100 entries plus unread counts, and the client pages backward with `gc.history { before, limit }` on demand. The client marks a hole in its local store so scrolling up fetches the middle.
4. A client offline for hours therefore costs one `subscribe` and a bounded replay, not the full history.

**Heartbeats.** WebSocket ping every 20 s from the server; any frame counts as liveness; no frame for 60 s closes the socket. Clients run their own app-level `ping` (the .NET `ClientWebSocket` ping behaviour on Android is unverified) and reconnect with jittered exponential backoff (1 s to 60 s).

**Backpressure [PROPOSED].** Each connection has a bounded outbound queue (default 1,000 events). On overflow the server does not buffer more: it stops live delivery for the slow streams and sends `lagged { s, lastSent }`; the client resubscribes from its cursor. The log is the truth, so nothing is lost. Inbound: max frame 256 KiB (attachments never travel on the socket), per-connection request rate limit, max 32 in-flight requests, `err { code: "rate_limited", retryAfter }`. Compression (permessage-deflate) is off by default; measure first.

**Versioning and compatibility [PROPOSED].** Protocol version is `major.minor`. Within a major: minors only add optional fields, new event types and new capability flags; receivers ignore unknown fields and unknown `evt` types; an unknown `req` type returns `err unsupported`. The server supports the current and previous major. If no overlap, the server sends `welcome` with `upgrade_required` and the minimum client build, then closes with 4426; clients show "update required" (update delivery is another doc). Capabilities are flags in `welcome`, not version checks.

**Server-driven layout and preferences [PROPOSED].** Clients keep no layout logic of their own beyond rendering known widget types.
- `ui.get { deviceClass, viewport, posture }` returns a `UiProfile`: start-screen composition (ordered widgets), navigation entries, enabled commands for the palette, theme reference, per-device overrides, and the user's stored preferences. The client reports facts (screen class, fold posture); the server chooses.
- `ui.changed { revision }` event after any change; the client re-fetches. Preference edits are requests (`prefs.set`); the server validates and stores them.
- Unknown widget types render as a placeholder, so an old client survives a new server. The client caches the last profile (with revision) only to paint something while offline.
- The theme file is stored and served by the server by revision (the theme doc owns the format).

**Auth hook [PROPOSED].**
```csharp
interface IConnectionAuthenticator {
  ValueTask<AuthResult> AuthenticateAsync(HelloCredential cred, DeviceInfo device, CancellationToken ct);
}
record AuthResult(bool Ok, PrincipalRef? Principal, string? DeviceId, DateTimeOffset? ExpiresAt, string? Reason);
```
Mid-connection: `auth.refresh` request before `ExpiresAt`; on expiry or revocation the server closes with 4401/4403 and the client re-pairs or refreshes. The auth doc supplies the implementation (pairing, tokens, passkey page).

**Status endpoint [PROPOSED].** `GET /status` (JSON, plus a minimal HTML view) and `GET /healthz`, read-only, on a separate listener config from `/ws` so limits do not share a bucket. Contents: server and protocol versions, uptime, database and cache provider health, agents online (count), queue depth, connected device count. No GC, code, Vault or agent names. Bind address and auth are OPEN (Bazaar.Server.md). The interactive sign-in page is a different surface owned by the auth doc.

**Android [PROPOSED].** The socket is held only while the app is foreground or unfolded. Otherwise:
1. Server sees a wake-worthy event (@mention, DM, decision awaiting the owner) for a device with no live socket and emits `IPushNotifier.WakeAsync(deviceId, WakeHint)`; coalesced to at most one per 30 s per device. The push carries no text.
2. The app wakes, opens a short-lived socket, runs `hello` + `subscribe` with its cursors, receives the content, builds the local notification, and closes.
3. Push tokens are registered via `device.registerPush { provider, token }`; the provider interface and FCM/UnifiedPush adapters belong to the Android doc.
Repeated or late pushes are harmless because the cursor makes the fetch idempotent. A `dataSync` foreground service (capped at 6 h per 24 h, per research) is never the notification path.

## Data and interfaces

```csharp
record Envelope(int V, string T, Kind K, string? Id, string? Re, string? S, long? N, DateTimeOffset Ts, ReadOnlyMemory<byte> P);
interface IWireCodec { string Name { get; } bool IsBinary { get; }
  void Encode(in Envelope e, IBufferWriter<byte> w); Envelope Decode(ReadOnlySpan<byte> frame);
  T DecodePayload<T>(in Envelope e); }
interface IPushNotifier { Task WakeAsync(string deviceId, WakeHint hint, CancellationToken ct); }
record WakeHint(string Kind); // no text; whether gc id and seq are included is Q9
```
Codec is chosen in `hello`/`welcome`: text frames = JSON, binary frames = Protobuf (only if the spike picks it). Per-device table `device_cursor(device_id, stream, applied_n)` is optional server-side bookkeeping for unread counts; the client's own cursor is authoritative for replay.

Close codes: 4400 bad frame, 4401 auth failed/expired, 4403 revoked, 4408 hello timeout, 4426 upgrade required, 4429 rate limited.

Message types (v1 sketch): `hello`, `welcome`, `subscribe`, `unsubscribe`, `gc.message`, `gc.send`, `gc.history`, `gc.read`, `resync`, `lagged`, `stream.revoked`, `presence.update`, `ui.get`, `ui.changed`, `prefs.set`, `auth.refresh`, `device.registerPush`, `ping`/`pong`.

## Behaviour and edge cases
- Ordering: per stream strictly by `n`; no ordering across streams.
- Dual path: live events come from the in-process dispatcher fed by the `LISTEN` connection; on a missed NOTIFY the dispatcher reads the log by `n`, so a lost notification delays but never drops.
- A client gets `err stale_cursor` if `after` is greater than the server's latest (restored DB): it must resync that stream.
- Sends: the response carries the assigned `n`; the echoed event arrives on the stream too and is de-duplicated by `id`.
- Limits (defaults, all configurable): 5 s hello timeout, 5 concurrent sockets per device identity (newest wins), 256 KiB frame, 1,000 queued events.
- Security: reject plaintext credentials on non-TLS unless loopback or an operator-allowed overlay; never log payloads; `err` messages leak no membership information (`denied` and "does not exist" look the same).
- Clock: clients never use local time for ordering.

## Testing
Real Postgres, in-process Kestrel with the ASP.NET Core test server and `ClientWebSocket`; fake clock; fake authenticator and fake push notifier. No models and no display.
- Replay: offline for 10k appended messages, reconnect, exact set and order, no duplicates; kill the socket mid-page, resume.
- Splice: appends during replay produce no gap or duplicate (property test over random interleavings).
- Cutoff: a gap over 2,000 yields `resync`, then `history` pages fill the hole.
- Idempotency: the same request `id` twice posts once.
- Slow consumer: stalled reader gets `lagged`, resumes with no loss.
- Versioning: unknown fields, unknown event, unknown request, no-overlap versions (4426).
- Auth: bad credential, expiry mid-session, revocation closes within 1 s, DM access denied is indistinguishable from missing.
- Heartbeat: silent peer closed at 60 s (fake clock).
- UI: `ui.get` per device class; an old client with an unknown widget renders the placeholder (Avalonia.Headless).
- Status endpoint: shape snapshot, contains no chat text, separate rate bucket.

**Wire-format decision (OPEN) and first spike.** Criteria, in priority order:

| Criterion | JSON + source generation | Protobuf |
|---|---|---|
| Non-.NET consumers (SDK/plugins) in the near term | weaker | stronger |
| Toolchain cost on all heads incl. Android | none | `.proto` codegen and a build step |
| AOT/trim safety on Android | proven pattern | to verify |
| Debuggability on the wire | high | needs tooling |
| Schema evolution rules | by convention + tests | built in |
| Size/CPU at our volume | measure | measure |

Spike plan (one throwaway branch, both codecs behind `IWireCodec`): (1) implement `hello`, `subscribe`, `gc.message`, `lagged` in both; (2) run the Testing cases above against each; (3) measure bytes and encode/decode time for a 200-message page and 10k-message replay on the NUC and on the Android foldable; (4) add a field and an unknown message to confirm forward compatibility; (5) check trimmer/AOT warnings in the Android head; (6) record LOC and build steps added. Decision rule [PROPOSED]: JSON wins unless a non-.NET consumer is a v1 requirement or the Android measurements show a problem JSON cannot fix.

Android spike items: `ClientWebSocket` behaviour in background; `adb shell dumpsys deviceidle force-idle` then wake push, fetch, notify; reconnect after network switch (Wi-Fi to mobile); path through the operator's overlay network.

## Work packages
1. `Bazaar.Contracts`: `Envelope`, kinds, close codes, error codes, message records, JSON source-gen context (depends on Wave 0).
2. `IWireCodec` + JSON codec + golden-file tests (WP1).
3. GC stream sequencing: per-GC counter in the append transaction + tests (data layer doc).
4. Server connection manager: upgrade, hello/welcome, auth hook, heartbeats, limits (WP1, 2).
5. Subscription engine: access check, replay paging, splice, `resync`, `history` (WP3, 4).
6. Dispatcher: `LISTEN` consumer, per-connection bounded queue, `lagged` (WP4).
7. Idempotent request layer (per-device id store) (WP4).
8. Client transport in `Bazaar.Client.Core`: reconnect with backoff, local cursor store, de-dup, ping (WP1, 2).
9. `ui.get`/`ui.changed`/`prefs.set` server + client handling with placeholder widgets (WP4; client shell doc).
10. Status endpoint and `/healthz` (independent).
11. `IPushNotifier` contract, wake coalescing, `device.registerPush` (WP4; Android doc).
12. Protobuf codec for the spike branch only (WP2); decision record afterwards.
13. In-process test harness: Kestrel + Postgres fixture + fake clock (WP2).

## Conflicts
- Bazaar.Server.md s3 still names "Protobuf message schemas" in the decision cell while saying reopened; the Server doc should be edited once the spike decides.
- Research suggests the push payload `{type, gc_id, seq}`; "content-free" is DECIDED, and a GC id plus seq reveals activity to Google. This doc defaults to a bare hint (Q9).
- "Clients learn nothing" (server decides layout) conflicts with a cold start offline and with Avalonia.Headless UI tests; hence the cached last profile and a minimal built-in fallback (Q5).
- Android doc and research dispute the `dataSync` 6 h cap; either way an always-on socket is not the plan. `ClientWebSocket` on Android background is unverified.
- Cloudflare Tunnel WebSocket fit is unverified (research note); if an operator uses it, long-lived sockets and heartbeats need testing.
- The research note leaves open whether Valkey/Garnet is needed on a single NUC; this design needs only an in-process dispatcher. Not a contradiction, but it weakens the case for the cache provider in the hot path.

## Questions for the owner
1. Gap-free per-GC counter (serializes writes per GC) vs a plain Postgres sequence with gaps tolerated. Recommend gap-free: simpler replay and gap detection; contention is negligible at this scale.
2. Replay cutoff before switching to `resync`: 2,000 entries or a time window (7 days)? Recommend 2,000 entries.
3. Should the first-spike decision rule favour JSON unless a non-.NET consumer is a v1 need? Recommend yes; also answer: is a non-.NET SDK or plugin planned for v1?
4. Do clients get one socket per device with newest-wins, or several (for example a detached editor window)? Recommend one socket, windows share it.
5. How much fallback layout may a client hold offline (last cached `UiProfile` plus a minimal built-in shell)? Recommend cached profile, shell limited to "connecting" and settings.
6. Is plaintext `ws://` allowed when the operator uses an overlay network, or must the server always have TLS? Recommend TLS required, with an explicit operator opt-out.
7. Heartbeat 20 s / timeout 60 s and backoff cap 60 s: acceptable? Recommend yes; mobile may need longer.
8. Status endpoint: what counts show (agent count, queue depth, device count), and is `/healthz` unauthenticated? Recommend counts only, unauthenticated on loopback/overlay bind, token otherwise. Bind rules stay OPEN.
9. Push payload: bare "wake" only, or also `{gc_id, seq}`? Recommend bare wake (no GC id), with the app fetching everything.
10. Wake rules: which events wake a sleeping phone (@mention, DM, owner decisions) and the coalescing window (30 s)? Recommend those three, 30 s.
11. Request-id retention for idempotency (24 h)? Recommend 24 h.
12. Frame limit 256 KiB with attachments on a separate channel (not designed here): acceptable? Recommend yes.
13. Compression: off until measured? Recommend off.
14. Should the previous protocol major stay supported (N and N-1)? Recommend yes, so a phone that has not updated still connects.
