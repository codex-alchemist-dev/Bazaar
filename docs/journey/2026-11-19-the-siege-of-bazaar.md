# The Siege of Bazaar (2026-11-19)

*Dev-journey battle log. Honest numbers, real bugs, a little drama.*

The owner said development was getting boring and that the dev journey needed lore. So for one day the mission was reversed: instead of building Bazaar, a crew of eight attackers was sent to destroy it.

This is what they did, what broke, what did not, and what is still standing with a crack in it.

## The battlefield

- One disposable cloud container: 4 cores, 15 GB of RAM. Nothing ran on the owner's PC.
- Bazaar.Server.Host built from `main` at `5f4a387`, Release.
- PostgreSQL 18 with pgvector. Each attacker got a private cluster, a private port and a private data root, plus one rule: kill only what you started.
- Weapons: `kill -9`, `SIGSTOP`, `pg_terminate_backend`, tmpfs volumes of 8 MB, `faketime`, `prlimit`, `stress-ng`, `iptables`, raw sockets, `hypothesis`, and a fake model server that lies on command.
- All eight ran at once on those 4 cores. The load average sat near 24 for most of the first half hour, which made every timing bug in the building come out to say hello.

Rules of engagement: a finding only counts with a repro that was actually run; every fix ships with a regression test; anything that needs a design decision is written down, not improvised (docs are law).

## The eight campaigns

| Campaign | Weapon of choice | Bugs fixed | Left for a decision |
|---|---|---|---|
| Crash-only | `kill -9` in a loop, SIGTERM, SIGSTOP | 5 | 2 |
| Postgres | Killing, freezing, corrupting and failing over the database | 4 | 2 |
| Wire fuzz | 22,000 hostile frames and a raw-socket toolbox | 3 | 0 |
| Disk, clock, resources | Full disks, time travel, 20 MB of heap | 5 | 4 |
| Migrations | 300 murders of `migrate` | 6 | 2 |
| Modules and Kodo | Truncation at every byte, bit flips, liar probes | 8 | 5 |
| Agent misbehaviour | A model that stalls, shouts and reports negative tokens | 6 | 2 |
| Concurrent load | Up to 2,000 clients, dead peers, partitions | 7 | 5 |

Several attackers found the same bug from different directions without knowing about each other. Those are counted once in the stories below.

## What broke

### 1. The two-headed server

*Found independently by three campaigns. That is how you know it was real.*

Bazaar promises one server per database. The guard is a Postgres advisory lock held on one connection. The lock check asked the connection object "are you open?", and a connection object whose server died stays politely "open" until somebody uses it. Nobody ever used it.

So: kill the Postgres postmaster, bring it back, and the host carries on with no lock while `/health/ready` reports `instance_lock: ok`. Start a second host. It takes the lock. Now two servers are ready on one database, each sure it is the only one.

**Fix:** the lock is verified against the database (its own session, in `pg_locks`), a watchdog checks every 5 seconds, a lost lock is taken again if it is free, and if another instance holds it the host stops with exit code 1. Verified live through a restart, an immediate stop and a failover to a promoted standby.

**Still true:** there is a window of up to 5 seconds. The lock does not fence writes; doc 03 leaves fencing to doc 33.

### 2. The kill switch that wasn't plugged in

The kill switch engine existed. Its tests passed. Its state survived restarts. And the one agent path that actually runs in the host today never asked it anything.

A global stop and an agent stop were stored for Charles, the host was restarted, and Charles was mentioned. Charles called the model and posted message 16 as if nothing had happened.

**Fix:** the responder checks the kill switch before the model call and again before the post, so a stop engaged mid-call still keeps the reply out. Three mentions while stopped: 0 model calls, 0 posts.

A second, nastier one hid behind it: an engage that landed *during* an agent resume was silently undone, leaving the cache saying "allowed" while the store said "stopped". The random-interleaving test failed 2 runs in 10 before the fix and 0 in 10 after.

### 3. The budget that overflowed into generosity

Report a spend of `long.MaxValue` tokens. The budget check adds it to the used amount, the sum wraps negative, negative is less than the limit, request approved. The largest possible bill was the only one guaranteed to pass.

**Fix:** overflow-safe arithmetic in the window check. Its cousin, an upstream reporting *negative* token counts, is now ignored and replaced by an estimate.

**Epilogue.** The owner read this one and was offered the sensible fix: gate the number and call it null. The reply: "Or we can over engineer and support millions of digits, decimals, and a number so astronomically large that it's honestly inappropriate. Because we love over engineering." A few minutes later: "why not. Make the disk the limit."

So budget amounts are now an exact decimal type that cannot wrap, stored as Postgres `numeric` (131,072 digits before the point, 16,383 after), and doc 48 section 11.4 specifies chunked rows in the same database for anything bigger, with the arithmetic streamed so that neither RAM nor a single field is the ceiling. A spend with more digits than the limit is refused from its digit count alone, so the only thing a hostile receipt can exhaust is its sender's patience. The chunked tier is designed, not built yet.

### 4. Death by polite shutdown

`kill -9` was survivable. Asking nicely was not.

SIGTERM with any client connected took 30.16 seconds, then the host aborted with exit code 134 and clients saw close code 1006. Sockets were never closed on stop, and when the shutdown timeout expired two hosted services threw out of `StopAsync` with nobody to catch them.

**Fix:** live sockets close with 1001 the moment the host starts stopping, and a requested stop can no longer crash the process. After the fix: exit 0 in 0.10 seconds, 25 SIGTERM cycles under load, all clean.

### 5. Exit code 134, the house special

A refused startup is a normal outcome. Bazaar treated every one of them as a crash: second instance, schema ahead of the build, edited checksum, unreachable database, a role without CREATE, a read-only session. Each printed the right sentence and then died with SIGABRT and a stack trace. An existing test asserted exit code 1; the real process returned 134.

Worse, on the second-instance path the refused host still started its lease-recovery sweep against the database it had just been told not to touch.

**Fix:** one Critical line, exit code 1, and nothing else starts. `migrate` got the same treatment for every database-level error, and no connection string or password appears on any error path (checked before and after).

### 6. The checksum nobody checked

`migrate` refuses a migration whose recorded checksum no longer matches. The host's startup gate compared names only. Edit a checksum in `schema_migrations` and `migrate` said no while the host said welcome.

**Fix:** the gate compares checksums too, and a history table with the wrong column types is refused with a clear message instead of a raw Postgres error. `migrate` also no longer waits forever behind another session's lock (default 30 seconds, then a clear refusal).

### 7. The 0-byte key

Fill the disk at the exact moment the host writes its first key-encryption key. The write fails, a 0-byte `kek.bin` stays behind, and the error was swallowed as "another process won the race". Every later start died with "KEK file does not hold a 32-byte key", even after the disk had space again. One full disk, one permanently bricked install.

**Fix:** write to a temp file, flush, then move it into place without overwrite.

### 8. Blobs on a bad day

On a full, read-only or wiped blob volume, uploads answered a bare 500, left `.part` and orphan `.meta` files behind, and wrote the owner record *after* moving the blob, so a blob could be committed that nobody was allowed to read. Wipe the blob root and every upload failed until a restart.

**Fix:** 507 `storage_unavailable` with Retry-After, the upload keeps its last good offset so the chunk can be resent, directories are recreated, and the owner record goes first.

### 9. Time travel

- `/status` reported an uptime of **-343,457,999 seconds** after the wall clock stepped back ten years. Uptime now uses the monotonic clock.
- With the host clock ten years ahead, recurring jobs were bucketed in 2036 and simply stopped running in 2026. Doc 03 says due times use database time; now they do.
- Kodo's circuit-breaker cooldown used wall time, so an NTP step of a few days moved it. Monotonic now.

### 10. Things a stranger on the wire could do

22,000 structure-aware fuzz cases found three ways to make the server blame itself for a client's nonsense:

- `hello` with `codecs: null` crashed the connection with close 1011 and a stack trace.
- `subscribe` with no stream list answered `internal`, which is the code that tells a client "retry". Each retry logged another stack trace.
- `POST /auth/refresh` without a JSON content type returned HTTP 500.

All three now get the documented client error (4400, `bad_request`, 400).

### 11. Edits, twice

- Two people editing one message at the same instant: one got `ok`, the other got `internal` and a duplicate-key stack trace (54 of 60 races). The loser now re-plans and becomes the next version.
- `kill -9` right after an edit committed, then retry with the same request id: the server answered "The text is unchanged" (11 times in 50 kill cycles), or handed back a version number that did not exist. The durable request id is now checked first, so the retry gets the original answer.

Both were found separately by the crash-only and the concurrent-load campaigns.

### 12. The herd that starved itself

The thundering-herd test restarted the host and had 2,000 clients reconnect and replay at once. After 400 seconds, 96 of them had finished.

The host was not busy. It was idle, waiting on itself. Each replay held one pooled database connection for its row reader and then asked for a second one to unwrap the author's key for each message body. A hundred overlapping replays each held one connection and waited for another that the other ninety-nine were holding. Everyone timed out after 15 seconds, politely, in turn.

**Fix:** read the rows, close the reader, then open the bodies. The same 2,000 clients now finish in 28 seconds.

### 13. The model that never hangs up

A fake model sent one chunk and then went silent. Sixteen minutes later the call was still open and still holding that GC's reply gate, so the agent was mute in that chat. The 120-second client timeout covered the headers, not the streamed body.

**Fix:** the timeout bounds the whole streamed call. (Idle timeout versus total deadline is listed below as a decision.)

Also from the fake model: an 8 MB agent reply was written into the append-only, never-purged GC log as a single 8,388,760-byte row. People are capped at 64 Ki characters; now agents are too.

### 14. Kodo's journal and the half line

A failed append to the Kodo spill file (disk full) left half a line. The next append glued itself onto it, and the file was unreadable forever after. Appends were not locked either.

**Fix:** the writer rolls back to the last complete record and serialises appenders.

### 15. Smaller victories

- A probe that blocks its thread before its first `await` hung the caller; the timeout never fired. Fixed in two places.
- .NET's `$` matches before a final newline, so `"postgresql\n"` and `"bazaar.gc\n"` passed id validators. Three regexes fixed; a sweep of the rest is on the list below.
- Installed `0.1.0-rc.10`, offered `0.1.0-rc.2`: accepted as an update, because pre-releases were compared as strings. Now SemVer 2.0 order.
- A component that threw half way through registration left half its services registered. Now all or nothing.
- A crashing line-move executor escaped with no audit entry and no rollback. Now it is a failed, audited step.
- A reply queue with no bound: a client that asks but never reads grew host memory by about 14 MB per second. Now it is closed with 1013. Queued events are bounded by bytes as well as by count.
- The request-id table kept every reply for 24 hours, including 100 KB history pages: 5,000 requests pinned 837 MB after the client had left. Replies over 16 KiB are now forgotten once delivered (they are all reads, and a retried read simply runs again).
- `/health/ready` hung for as long as Postgres was frozen. Each check is now bounded and the answer is 503.
- A corrupt heap page made every subscribe answer `lagged` with nothing in the log. It is logged now.

## What held

This is the part that should make the builders smile.

- **60 `kill -9` cycles under load: 25,584 acked messages and edits, 0 lost, 0 duplicated, no sequence gap.** Replay from a cursor matched Postgres exactly. Time back to ready: 0.56 s best, 2.40 s worst.
- **300 kills of `migrate`** (200 process kills, 100 backend kills): every rerun converged on a schema byte-identical to a clean run.
- **20 simultaneous `migrate` runs:** exactly one applied all 52 migrations.
- **Postgres disk full, WAL PANIC, crash recovery failing too:** 0 of 257 acked messages lost, twice.
- **Failover to a promoted standby:** the host recovered by itself in about 6 seconds. 910 acked, 0 lost.
- **Kodo journal:** truncated at every byte offset, it recovered exactly the written prefix. Every single-bit flip was detected. 20,000 fuzzed inputs produced only the one documented exception.
- **Signed modules:** every tampered field refused, every signature bit matters, revoked and expired keys refused.
- **Budget races:** 64 racers for 1 remaining token, 50 rounds, exactly 1 admitted every time.
- **Approvals:** forged, replayed and retargeted grants all failed.
- **Loop guard:** agent ping-pong cut at 6 messages.
- **Auth and access:** constant-time token compare, no data from a GC you are not in, no file ever served from a traversal path, **no secret in `/status`, `/health/ready` or any error output.**
- **2,000 clients on one GC, and 6,000 subscriptions across 200 GCs:** every subscriber saw every message exactly once and in order, through more than 250 reconnects with a cursor.
- **Postgres partitions of 5, 25, 60 and 300 seconds:** writers retried the same ids. 0 duplicates, 0 lost acks.
- **100,000 notifications queued while the host was frozen:** nothing lost. Commits with no notification at all were found by the 30-second sweep.
- **Dead peers:** half the clients silently dropped off the network and were reaped at 60 seconds with host memory flat.
- **20 MB of heap:** requests failed one at a time, the process lived, nothing was corrupted.
- **A 100 MB frame:** cut off after about 3 MB. Never buffered.

## The attackers' own casualties

Honesty section.

- The crash-only attacker reported corrupt blobs. The corruption was in its own script (threads sharing one list slot). The server was innocent.
- Two regressions came from our own fixes and were caught before anything was pushed: the atomic component registration wrapped exceptions in a new type (a composition test noticed), and the new kebab-case id check collided with an older test that used `work_life`.
- Three campaigns fixed the two-headed server three different ways. One fix was kept.
- Docker was not available in the container, so tests ran against real local Postgres clusters instead of Testcontainers.

## The second wave (same evening)

While the first pull request waited in the runner queue, three more campaigns went after what the first eight had not touched: hostile input on the other HTTP endpoints, races outside the GC log, and every file writer the first disk attack skipped. Twelve more real bugs, all fixed with tests.

- **The open door at `/auth/refresh`.** It takes no credential, and it buffered whatever a stranger sent, up to 30 MB per connection. Twenty held bodies added about 530 MB to the host. A body over 16 KiB is now refused with 413 before it is read.
- **Blobs, again.** Two uploads of the same bytes at the same instant gave the loser a 507 in half the runs. A quota of 3 uploads let 40 racers open 4 or 5. An abort between the last chunk and the commit stranded the upload for 24 hours. A client that hung up mid-chunk was told the disk was full. All four fixed.
- **The Vault kept what it failed to commit.** When a commit failed (a stale `index.lock`, a full disk), the new decision file or the half-moved document stayed in the checkout, and the next commit swept it in. Failed writes now roll the tree back.
- **One torn backup sidecar stopped all backup cleanup.** Set discovery threw, every later run was recorded as failed, and retention and WAL cleanup never ran again. The sidecar is written atomically now and a torn one is skipped.
- **A failed export blocked its own retry** ("directory is not empty"). The partial export is now removed and the manifest is written last.
- **The FreeLLMAPI sidecar accepted an empty encryption key** from a zero-byte key file, and wrote its secrets in place. Now: temp file, owner-only mode, rename, and a key that is not 64 hex digits is refused without being echoed.
- **Secrets mounted for agents and the Postgres version file** were written in place. Now atomic.
- **Four more `$` validators** on names that become paths accepted a trailing newline.
- **Stopping the host burned job attempts.** A stop in the middle of a claimed batch left the unstarted jobs leased, and each lost an attempt. Five restarts could fail a job whose handler never ran. Unstarted jobs are now given back with the attempt refunded.

What held in the second wave: 12,642 hostile requests against a running host with no 5xx, no hang and no state created by an unauthenticated caller; the new giant-amount intake under 33 kinds of broken body; Council votes with every voter double-casting (one ballot each, one reveal); 56 Kanban edges inserted at once with no cycle; 16 claimers of one card (1 wins, 15 conflicts); 12 parallel refreshes of one credential (exactly one new pair); and the large-amount sweeper racing ten writers with no orphan chunk.

New cracks from the second wave are items 16 to 21 below.

## The third wave (that night)

Three more campaigns, each on ground the first two had not walked: who may see and do what, what a legitimate member can make the server spend, and what strange data does once it is stored. Fourteen more real bugs, all fixed with tests.

- **The Constellation showed everyone everything.** `POST /constellation/tiles` took no principal and laid out the whole Vault graph, so any signed-in member got the ids, links and positions of every note, including other agents' private memory. Doc 19 already said the projection is per viewer; nothing did it. The endpoint was first shut to everyone but the owner, then given the real fix: the graph is now built from the notes the caller may read, by the same SQL access clause Vault search uses, and an edge survives only when both ends are readable. The layout runs over the visible notes only, so a hidden note cannot even nudge a visible one. A graph source that knows nothing about viewers gives non-owners an empty graph.
- **Removed members kept their open streams.** `gc.member.add` and `gc.member.remove` did not re-check live subscriptions, so a removed member went on receiving the GC until they reconnected. Membership changes now re-check and revoke.
- **One principal could answer for another.** The retry table was keyed by device id alone, and device ids are free text, so a member who guessed another's device id and request id could be handed their stored reply. The table and the per-device socket count are now scoped to the principal.
- **Reading a busy GC cost the whole GC.** A replay page scanned every message to find the edited ones. With 300,000 messages a single page took seconds. It now costs only the new and edited messages.
- **Stream names had more than one spelling.** `GC:` in capitals, padded ids and braces all reached the access check as different names for one stream. Anything but the canonical spelling is refused.
- **`gc.send` skipped the byte cap that `gc.edit` enforces.** A message could be sent at four times the size it could be edited to. Both now stop at 32 KiB.
- **The NUL byte.** Postgres text cannot hold one. In a search query it came back as a 500. In a model's reply or tool arguments it escaped the turn runner and left the step pending for ever. In a job's error text it made the failure report itself fail, so the job stayed leased. In a GC title, a trigger purpose or a Kanban reason it threw out of the tool. Each is now a clean refusal or a clean failure.
- **A schedule too far away.** `every=P3650000D` pushed the next occurrence past the end of time and threw out of trigger creation and the tick. It now clamps to "never".
- **Digits that are not digits.** `\d` accepts Arabic-Indic and other Unicode digits. A stray decision file named with them made the number parser throw and blocked every later decision write. About fifteen validators now say `[0-9]` and end with `\z`, which also closes item 11's regex sweep for the ones found.
- **Cutting text through a character.** Job errors and GC titles were cut at a fixed length that could split a surrogate pair, which Postgres refuses. Pairs are kept whole.

What held in the third wave: every GC operation by a non-member answers the same as for a GC that does not exist; nobody can edit another author's message or reuse another author's retry key; owner-only handlers decide by the authenticated kind, never the payload; another principal's upload id looks unknown; Vault search and document fetch apply the access rule in SQL on every query; mention handles are ASCII, so no look-alike @mention; no SQL is built from a value anywhere in `src`; the append-only triggers cover every partition for the app role; and daylight-saving sweeps over seven awkward zones never fired a trigger twice or skipped one.

New cracks from the third wave are items 22 to 36 below.

## The fourth wave (late that night)

Until now every attack had been aimed at the server. The fourth wave turned around: the client facing a hostile server, the parsers that read mail and Markdown written by strangers, and the install, the bundled Postgres and the agent that lives inside each VM. Twenty-two more real bugs, all fixed with tests.

- **A client that could be hung for good.** After a terminal close (4401, 4403, 4426) every waiting request, and every later one, waited for ever. A server that never said `welcome`, or stopped reading, held the connection open indefinitely because the heartbeat's own ping blocked on the stuck socket. A connect that timed out inside the transport stopped the client permanently as "cancelled". All three now fail fast or reconnect; the handshake has a 30 second limit.
- **A server could fill the client's disk.** Events for streams the client never subscribed to were delivered and each made a cursor entry in the saved cursor file. They are now dropped. The relay socket also buffered an endless fragmented message (64 MiB in the test); it stops at 64 KiB.
- **Failures that took the app down or said nothing.** A refused tile endpoint threw into an `async void` handler, which ends the process. The Vault pane swallowed garbled answers and kept showing stale results. Both now degrade to a status line.
- **Mail attachments that were not called attachments.** The parser listed only parts marked `attachment`, so an executable sent inline skipped the executable screen and the attachment count. `invoice.exe` followed by a NUL and `.pdf` was screened as a PDF. Link targets written as `//host`, `https:host` or `javascript:` had no host as far as the check could tell, so a link whose text said one site and whose target was another went unnoticed. All three closed.
- **The signed mail queue.** A correctly signed but malformed batch threw five different unplanned exceptions. A quoted `@` in a recipient address (the sender controls it) made the whole batch fail on every pull. Mailbox matching was case-sensitive, so mail to a differently cased address was dropped.
- **One Markdown file could stop the Vault index.** A committed note with 5,000 nested quotes made the Markdown parser throw an exception the indexer did not expect, so every catch-up failed and the index never advanced again. The file is now marked invalid and skipped.
- **The guest agent died on `{}`.** A frame with missing fields threw a null reference out of the read loop and killed the agent. An exec with a timeout of `int.MaxValue` threw after the process had started, killing the agent and orphaning the child.
- **The bundled Postgres trusted a stale pid file.** If the pid had been reused by another program, the manager adopted it, timed out, and could later kill that unrelated program. It now checks the process is Postgres.
- **The managed superuser password** was saved through one fixed temp name, so two saves at once raced. Now a unique owner-only temp file, flushed and moved into place.
- **A VM stuck in "provisioning" for ever** if the host stopped at the wrong moment: the reconciler skipped it and retire refused it. The reconciler now returns it to absent.
- **The clock, again.** After the clock stepped back a day the rate limiter stayed shut for a day against a one-minute window.
- **Smaller:** the setup plan loader threw on a `null` list item; a heartbeat gap or a token lifetime large enough to overflow was accepted at start and failed later.

What held in the fourth wave: update version checks (downgrade, insecure URL, hash and signature); header injection on send and every MIME bomb tried; GitHub webhook signatures and dedupe; the Rubedo parser under deep nesting; 300,000 fuzzed inputs each against the mDNS, STUN, signalling and relay codecs; TLS certificate loading with wrong keys and garbage; and the guest agent against a file write aimed at a directory, absurd terminal sizes and NUL in arguments.

New cracks from the fourth wave are items 37 to 50 below.

## Still standing, with cracks (needs the owner or a design doc)

None of these were changed. Each needs a decision first.

1. **A missing data root mints a new KEK.** If the data root is not mounted at start, the host silently creates a fresh key. Existing wrapped keys no longer unwrap, every send fails with `internal`, and readiness stays green. This was the most dangerous open item. **Closed on 2026-11-22 without waiting for the decision:** the host now refuses to start when the database holds wrapped keys and its key file opens none of them (doc 03 startup step 4a). Still open: the host can create a key file at start at all (doc 63 proposes that only setup may).
2. **The blob store ignores the host data root** and falls back to a per-user folder, so two installs on one machine share blobs and a data-root backup misses them.
3. **Approval grants are marked used after the call runs**, so two concurrent dispatches could both use one grant. Not wired into the dispatcher yet.
4. **Not wired yet:** nothing calls the token budget service, nothing can engage the kill switch except SQL, and usage is kept in memory rather than in `gw_usage`.
5. **Connection exhaustion:** with a tiny `max_connections`, about 20% of sends fail with `internal` and no retry hint. There is no retryable wire code for "database busy".
6. **Schema drift below the history table** (a dropped column or table) passes the startup gate. Kodo's checksum self-check is planned, not built.
7. **No repair path** when migration history rows are deleted: both the host and `migrate` refuse, and only manual SQL helps.
8. **Host clock versus database clock:** doc 03 says leases and due times use database time; several places still stamp host time.
9. **Kodo evaluator:** hysteresis counts caller wall time, the flap rule can move `recovering` to `degraded` (not a row in doc 46 section 8.2), and a truncated journal tail cannot be told from a crash until checkpoints and anchors exist.
10. **Stream timeout:** total deadline (what the fix does) or idle timeout? The Anthropic and Gemini adapters share the pattern and were not changed.
11. **The `$` regex sweep** across the remaining validators.
12. **Replay pages are not bounded by bytes.** 200 stalled replays of a GC with 30 KB messages still took the host to about 3 GB. The same run burned 34 to 57% of a core with the cause not found.
13. **The replay cutoff counts a new message twice**, so `resync` triggers at about 1,000 missed messages, not the documented 2,000. And the `gc.edit` reply carries the edit event's sequence number where doc 02 says the original `n`.
14. **Memory creep:** 159 MB to about 570 MB over 15 cycles of 400 connects and disconnects, with file descriptors and threads flat. Part of it was the reply table (now fixed); the rest is unattributed.
15. **File descriptor exhaustion:** the accept loop spins at about 86% of a core and `/health/live` goes dark until sockets free up.
16. **GC sender membership is checked outside the log lock.** A member removed at sequence N can still land a message at N+1. Found by reading the code, not by a timed race. Doc 01 does not say which ordering is right.
17. **Council state changes and their GC events are two steps.** A crash between them leaves no event, and only the reveal is replayed.
18. **A base backup that crashes mid-copy** leaves a `set-*` folder with no sidecar. It is ignored and never deleted. And a restore that fails part-way leaves a non-empty target, so the retry is refused.
19. **Large-amount intake:** nothing deletes an intake amount that no limit refers to any more, and with the guard set to unlimited the route has no body cap (it is owner-only and rate limited).
20. **`/auth/refresh` has no per-address limit.** The 16 KiB body cap is this campaign's choice; doc 32 gives no size.
21. **Is a zero-byte blob valid?** Today an upload of length 0 is refused with 400.
22. **Small replies are kept for 24 hours with no total bound.** A connection that asks for history pages just under 16 KiB at the rate limit pins about 2.9 GB an hour. It needs a per-device budget or a count cap, and the number is a design call.
23. **No cap on streams per `subscribe`**, no member cap on an ordinary GC, and **no write quota per principal** (the rate limit is per connection, and connections are free). The GC log is never purged, so a member can grow it at will. Doc 15's shared buckets are not built for the wire.
24. **Agent posts may still be 64,000 characters** while members stop at 32 KiB.
25. **May the owner post into a DM they are not in?** Today yes. Doc 01 calls the owner a reader.
26. **The stream access check treats every principal as a human**, so the owner cannot subscribe to a GC they are not a member of and an agent cannot subscribe to its own Thinking GC. It fails closed, but it contradicts "the owner reads all" until identity supplies kinds.
27. **An observer cannot leave a GC**, and **any member can re-add someone the owner removed.** Doc 01 is silent on both.
28. **`gc.member.add` does not check that the added id is a real principal** or that its stated kind is true. Waiting on the identity service.
29. **Revoking a device is not wired:** nothing calls it, it does not check who asks, and it does not close open sockets. Doc 32 sections 5 and 5a are still PROPOSED.
30. **When attachments land,** the writer must check that the sender may read the blob it links, or any member gains every blob whose hash they can name.
31. **The Constellation level-of-detail feed** is unregistered and still reads the whole graph. It must take the viewer before it is switched on. And coordinates now differ between viewers; a layout shared by all viewers would leak hidden structure, so that is a design choice.
32. **`reply_to` is not checked.** A reply to message -5, or to one beyond the head, is stored.
33. **The table owner can `TRUNCATE` a single partition** of the GC log: the guard is on the parent only. The app role cannot. The fix is a new migration, and `common/` is frozen, so where it goes needs deciding.
34. **About eight more places cut text by UTF-16 unit.** None write to Postgres text today. A shared "cut without splitting a character" helper would end the class; where it lives needs deciding.
35. **Agent-made daily schedules step in UTC**, not the agent's time zone: the tool passes no zone.
36. **Smaller:** the wire decoder accepts duplicate fields and trailing bytes; two regexes (path globs, the Constitution heading) are slow on crafted input and only one has a timeout; a former member is refused history but served search up to the point they left.
37. **A server can poison a client's cursor.** A message numbered `long.MaxValue` pins the cursor so every later message is dropped, and a resync never lowers it. A live jump of more than one is not treated as a hole either.
38. **One undecodable frame makes the client reconnect about once a second for ever.** The attempt counter resets on every `welcome`.
39. **The client trusts the heartbeat values in `welcome`.** An interval over about 49 days kills the heartbeat silently; a huge timeout switches silence detection off. It needs a clamp, and a number.
40. **The client has no size cap on HTTP bodies** (tiles, Vault, the outside probe), and its message and resync queues are unbounded.
41. **Do inline images count toward the attachment limit?** They do now, so ordinary HTML mail with more than five images would be held. Doc 22 does not say.
42. **A malformed item inside a validly signed mail batch** refuses the whole batch. Skip and quarantine the one item instead?
43. **`Authentication-Results` is read from the first header of that name.** If the receiver adds none, the sender's own claim is trusted.
44. **Smaller mail items:** a header over 16 KiB escapes as the wrong exception; text is always decoded as UTF-8 whatever the charset says; the IMAP back end has no size cap before the limit check and was not tested live.
45. **The Vault indexer has no file size cap,** and a 2.5 MB note of deeply nested lists takes the Markdown parser 7.5 seconds.
46. **A bundled Postgres that crashed half way through its first initialisation** refuses to start and refuses to initialise. Recovery is by hand; wiping automatically would be destructive.
47. **Nothing times out a VM back end call or the keyring tool.** One hung call blocks that VM and the whole start-up reconcile; a locked keyring blocks host start.
48. **Expired TLS certificates load without a word,** and a key file readable by group or world is accepted silently.
49. **The guest's nonce is on its command line,** readable by other users in the guest. The server side of the guest handshake is not built, so identity claims could not be attacked yet.
50. **If the final audit write of a setup run fails** after every step applied, nothing rolls back.

## How to do it again

Every attack that could become a test did:

- `tests/Bazaar.Kodo.Tests/Chaos/`: journal truncation and mutation, probe chaos, evaluator property tests.
- `tests/Bazaar.Server.Modules.Tests/Chaos/` and neighbours: manifest, signature, line-move and module-health chaos.
- `tests/Bazaar.Server.Tests/`: `GcLogWriterKillTests`, `MigrationFaultTests`, `KillSwitchChaosTests`, `TokenBudgetChaosTests`, `ApprovalChaosTests`, `OpenAiChatAdapterChaosTests`, and the wire and HTTP regression tests.
- `tests/Bazaar.Server.Host.Tests/`: `InstanceLockTests`, `HostRunTests`, `StartupSequenceTests`.

These are the first rows of the doc 46 section 17.1 fault harness. The owner-enabled chaos mode of section 17.3 (signed experiment catalogue, sandbox copy, resilience score) still depends on the sandbox tier and is not built.

## Closing

Bazaar did not lose a single acknowledged message all day. It did lose its dignity a few times: it grew a second head, ignored its own kill switch, and reported that it had been running for minus eleven years.

It is harder to kill now than it was this morning.
