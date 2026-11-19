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
| Concurrent load | Up to 2,000 clients, dead peers, partitions | see below | see below |

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

### 12. The model that never hangs up

A fake model sent one chunk and then went silent. Sixteen minutes later the call was still open and still holding that GC's reply gate, so the agent was mute in that chat. The 120-second client timeout covered the headers, not the streamed body.

**Fix:** the timeout bounds the whole streamed call. (Idle timeout versus total deadline is listed below as a decision.)

Also from the fake model: an 8 MB agent reply was written into the append-only, never-purged GC log as a single 8,388,760-byte row. People are capped at 64 Ki characters; now agents are too.

### 13. Kodo's journal and the half line

A failed append to the Kodo spill file (disk full) left half a line. The next append glued itself onto it, and the file was unreadable forever after. Appends were not locked either.

**Fix:** the writer rolls back to the last complete record and serialises appenders.

### 14. Smaller victories

- A probe that blocks its thread before its first `await` hung the caller; the timeout never fired. Fixed in two places.
- .NET's `$` matches before a final newline, so `"postgresql\n"` and `"bazaar.gc\n"` passed id validators. Three regexes fixed; a sweep of the rest is on the list below.
- Installed `0.1.0-rc.10`, offered `0.1.0-rc.2`: accepted as an update, because pre-releases were compared as strings. Now SemVer 2.0 order.
- A component that threw half way through registration left half its services registered. Now all or nothing.
- A crashing line-move executor escaped with no audit entry and no rollback. Now it is a failed, audited step.
- A reply queue with no bound: a client that asks but never reads grew host memory by about 14 MB per second. Now it is closed with 1013.
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
- **20 MB of heap:** requests failed one at a time, the process lived, nothing was corrupted.
- **A 100 MB frame:** cut off after about 3 MB. Never buffered.

## The attackers' own casualties

Honesty section.

- The crash-only attacker reported corrupt blobs. The corruption was in its own script (threads sharing one list slot). The server was innocent.
- Two regressions came from our own fixes and were caught before anything was pushed: the atomic component registration wrapped exceptions in a new type (a composition test noticed), and the new kebab-case id check collided with an older test that used `work_life`.
- Three campaigns fixed the two-headed server three different ways. One fix was kept.
- Docker was not available in the container, so tests ran against real local Postgres clusters instead of Testcontainers.

## Still standing, with cracks (needs the owner or a design doc)

None of these were changed. Each needs a decision first.

1. **A missing data root mints a new KEK.** If the data root is not mounted at start, the host silently creates a fresh key. Existing wrapped keys no longer unwrap, every send fails with `internal`, and readiness stays green. This is the most dangerous open item.
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
12. **File descriptor exhaustion:** the accept loop spins at about 86% of a core and `/health/live` goes dark until sockets free up.

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
