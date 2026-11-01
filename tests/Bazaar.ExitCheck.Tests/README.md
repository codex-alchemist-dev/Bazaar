# Stage 3 exit check

The protocol test that decides whether Stage 3 is done (plan Batch 3, item E2E; doc 34 layer L3). On real PostgreSQL, with the
real wire module and GC glue on in-process Kestrel and the real `WireClient`:

1. a scripted fake agent asks its model through the policy-checked gateway and posts the answer to a GC;
2. a client drops its socket, reconnects and replays from its cursor with no lost message and no duplicate;
3. a retried send over a dropped socket posts once;
4. a model call outside the agent's whitelist is denied by policy and nothing reaches the provider or the GC.

It runs in `Bazaar.slnx` and gates merges like any other test project. Windows without `BAZAAR_TEST_PG` skips the database
tests, as the rest of the suite does (`test.ci.windows_db_job = false`).

`ExitCheckLedger.cs` records the work packages the exit check needed; all are merged, and a test pins the list.

Run locally: `BAZAAR_TEST_PG='Host=localhost;Username=postgres;Password=...' dotnet test --project tests/Bazaar.ExitCheck.Tests`.
