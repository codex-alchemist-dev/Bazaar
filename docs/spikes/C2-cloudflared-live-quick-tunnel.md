# Spike C2: Conduit `cloudflared` module over a real quick tunnel

Design source: `docs/design/39-conduit-remote-connectivity.md` (section 2, `cloudflared`, quick mode; section 3, third-party TLS termination; testing section). Code: `src/Bazaar.Conduit/Cloudflared/`, harness in `tests/Bazaar.Conduit.LiveTests/`.

## Question
Does `CloudflaredModule`, with the production `SystemCloudflaredLauncher`, publish a working offer on a live free tunnel that needs no account, and does a WebSocket round trip through it work?

## Pass criteria (fixed before the first run)
- The module reaches `Running` and publishes one `wss://<host>/ws` offer with `TerminatesTls` true.
- A WebSocket client connects to that offer and gets 20 of 20 echoes from a local echo server.
- Connect retries and round-trip times are recorded.

## Method
`BAZAAR_CONDUIT_LIVE=1 dotnet run --project tests/Bazaar.Conduit.LiveTests` (optional `BAZAAR_CONDUIT_CLOUDFLARED` for the binary path, `BAZAAR_CONDUIT_REPORT` for a Markdown report). The harness starts a loopback echo server, runs the module in quick mode, retries the tunnel start up to 3 times and the first WebSocket connect with backoff, then sends 20 pings. The test skips itself when the opt-in variable is unset, `cloudflared` is not runnable, or the Cloudflare edge (`region1.v2.argotunnel.com:7844`) is not reachable. Provider: Cloudflare quick tunnel only (free, no account). The `ssh` module needs an operator-owned jump host, so it is not a no-account provider.

## Results
Run on the operator PC (Windows, Wi-Fi, `cloudflared` 2026.11.1), three runs about 3 minutes apart:

| Run | Offer published | Connect attempts | First echo | Echoes | RTT min / p50 / p95 / max (ms) |
|---|---|---|---|---|---|
| 1 | 3.5 s | 9 | 64.7 s | 20 of 20 | 37 / 47 / 67 / 75 |
| 2 | 3.8 s | 9 | 65.1 s | 20 of 20 | 35 / 36 / 37 / 39 |
| 3 | 3.7 s | 9 | 64.9 s | 20 of 20 | 74 / 75 / 79 / 89 |

The PC had a Wintun adapter up, so round trips may include a VPN hop. Every run passed all criteria.

Cloud container: the test skips there. Outbound port 7844 to the Cloudflare edge is refused, so a quick tunnel cannot start from a restricted-egress host. Hosted CI therefore skips this test too, by design.

## Findings
1. **Result: pass.** The module, launcher, URL parser and offer work against the real binary.
2. **Propagation delay.** The offer is published in about 4 s, but the new hostname only answered after about 65 s in every run (9 connect attempts). A client or the wizard's reachability probe must retry for 60 to 90 s before calling the tunnel broken. The harness allows about 3 minutes.
3. The offer carried `TerminatesTls` true, as doc 39 section 3 requires.
4. Quick-tunnel hostnames change on restart; this was not exercised beyond restart-per-run.
5. Not covered: named tunnels (need an operator domain and token), long soak, restart supervision against the real binary.

## Follow-ups
- Make the wizard's reachability probe retry for at least 90 s (doc 39 testing section).
- Optional later: a nightly soak on the operator PC.
