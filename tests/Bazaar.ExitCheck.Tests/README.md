# v0.3 exit check

The protocol test that decides whether v0.3 is done (plan Batch 3, item E2E; doc 34 layer L3). On real PostgreSQL:

1. a scripted fake agent asks its model through the policy-checked gateway and posts the answer to a GC;
2. a client drops its socket, reconnects and replays from its cursor with no lost message and no duplicate;
3. a model call outside the agent's whitelist is denied by policy and nothing reaches the provider or the GC.

## Expected to fail until ready

Doc 34 forbids `Skip`, quarantine and retry-to-green, and a skipped or fake-passing exit check would hide the gap.
So the server-backed tests **fail on purpose** with `PendingPrerequisitesException`, which lists the unmerged work
packages from `ExitCheckLedger.cs`. This project is not in `Bazaar.slnx` (so `build.sh` and required checks stay green)
and runs in the non-blocking CI job "v0.3 exit check (tracking)".

The policy denial through the merged GW7 resolver and GW10 adapter runs for real today.

## Graduating

When a prerequisite merges, set its `Merged` flag in `ExitCheckLedger.cs` in the same PR. When PRH lands, replace the
last `throw` in `ExitCheckServers.StartAsync` with the harness (host on `db`, migrations applied, one GC, a human and an
agent member, a fake authenticator behind `CredentialFor`). When the whole project passes on a clean run:

1. add the project to `Bazaar.slnx`;
2. delete the tracking job from `.github/workflows/ci.yml`;
3. delete this section.

Run locally: `BAZAAR_TEST_PG='Host=localhost;Username=postgres;Password=...' dotnet test --project tests/Bazaar.ExitCheck.Tests`.
