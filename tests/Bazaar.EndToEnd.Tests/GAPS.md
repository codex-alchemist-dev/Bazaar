# End-to-end gaps

Found while building the smoke harness (`Bazaar.EndToEnd.Tests`). Each gap is also a skipped test in `GapTests.cs`. The harness
does not work around a gap silently: where it has to stand in for a missing step, the stand-in is named here.

What the harness proves today, on every Linux CI run: the real `Bazaar.Server.Host` process migrates an empty Postgres database,
starts, reports ready, refuses a wrong token, accepts the dev token, lets a headless wire client send a mention in a group chat, has
the agent answer through the real gateway (FreeLLMAPI source pointed at a keyless stub model), writes the reply to the log, and
writes it to the log.

| # | Gap | Stand-in used by the harness | Needed from |
|---|---|---|---|
| 1 | No first-run install path to run. The host starts only from hand-written configuration. The setup wizard (#824, #914, #952 and later), the managed Postgres bundle and service registration are not run end to end. | External Postgres from `BAZAAR_TEST_PG`, `migrate` run by hand, config as command-line keys. | Setup thread, Packaging thread |
| 2 | Nothing creates a company, a group chat or an agent at run time: no wire request, HTTP route or setup step. There is no agent record either; the responder reads its agents from configuration and the policy comes from the dev seed. | Rows for one GC and two members written straight into `gc` and `gc_member`; one agent listed under `Bazaar:AgentResponder:Agents`; policy from `DevAgentPolicySeed`. | Company, Souls and Identity threads. Update 09:16Z: the client thread's #1086 uses `POST /setup/plans` and `/setup/plans/{hash}/apply` with a bootstrap code for company creation; once that merges, the harness should create the company that way. |
| 3 | Sign-in is the dev bearer token (`Bazaar:DevAuth`). Pairing, signed access tokens and refresh (#797, #807, #974) are not exercised, and the dev token is refused outside local development by design. | `Bazaar:DevAuth` with a token from an environment variable. | Remote auth thread. `POST /auth/handoff` (client #1086) is PROPOSED and not built server-side. |
| 4 | The "client" is `WireClient` from `Bazaar.Client.Abstractions` only. No sign-in screen, no create-company screen and no chat view runs headless against a real server. | A scripted `WireClient`. | GUI shell thread (`Avalonia.Headless`) |
| 5 | The agent task is a chat reply. A card on the board, the turn loop, the coding harness and a GitHub merge are not exercised. | One mention, one model call. | Agents, coding and GitHub threads |
| 6 | **Bug.** Model usage is silently dropped. The gateway inserts into `gw_usage`, which has a foreign key to `gw_provider`; the FreeLLMAPI provider defined by configuration has no `gw_provider` row, so Postgres refuses the insert (`gw_usage_provider_id_fkey`) and the gateway swallows the error (`RecordSafeAsync` reports it only to an internal-error callback nobody logs). Quotas, token budgets and cost chips that read `gw_usage` see nothing. Fix: write the overlay provider row at start (or drop the foreign key), and log the swallowed error. | None. Skipped test `The_model_call_is_metered_as_pooled_usage_for_the_agent` is ready to unskip. | Gateway thread |
| 7 | The service-host provider reports "no usable provider" on a plain Linux box (systemd not running). Fine for CI, but the log says it at info level, not as a state the operator can act on. | None. | Host thread |
| 8 | `KekConfigured=False` in the start log, yet GC bodies are written (a body key exists some other way). It is unclear whether a server without a configured KEK is meant to start. | None. | Security thread |

Open follow-ups for the push lead: gaps 2 and 3 block "create a company" and "sign in" for a real person; gap 4 blocks any claim
that a client works end to end.

The Claude Code provider (#1066) has a fake CLI under `tests/Bazaar.Server.Providers.ClaudeCode.FakeCli`; the harness uses the FreeLLMAPI stub instead, which needs no merged PR. A second run on the `claude-code` route can be added after #1066 merges.
