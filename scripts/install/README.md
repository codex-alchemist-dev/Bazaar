# Clean-machine install (Linux)

`install-linux.sh` takes a bare Debian or Ubuntu machine to a running, migrated `Bazaar.Server.Host`, with no prompts,
and logs every step (docs 03, 37 and 45 sections 1.1, 1.5 and 1.9).

    sudo scripts/install/install-linux.sh [--prefix DIR] [--log FILE] [--skip-prereqs] [--keep-running]

What it does, in order: preflight (OS, disk), PostgreSQL 18 with pgvector from the PGDG repository, the .NET SDK named in
`global.json`, a `bazaar` database and role (password generated into a 0600 file, masked in the log), `dotnet publish` of the
host, `migrate`, host start, a setup plan sent through `POST /setup/plans` and `POST /setup/plans/{hash}/apply`, then smoke
checks on `/health/live`, `/health/ready` and `/status`. Exit 0 means the smoke checks passed.

Files go under `--prefix` (default `/opt/bazaar`): `app/`, `data/`, `secrets/` (0700), `host.log`, `bazaar-setup.json`.

## Known limits

- **Apply is not complete on main.** Until the setup wizard apply steps land (doc 45 section 1.9), apply of a plan that
  changes anything ends in exit code 3 (HTTP 500). The script logs it and still passes if the smoke checks pass. When apply
  succeeds the log says so.
- **The bootstrap code is single use.** Validating and applying are two calls, so the script restarts the host between them
  to get a fresh code. A real unattended flow should either accept `--config` on the host or let validate not consume the code
  (a question for the setup wizard thread).
- **Linux only.** The Windows install is covered by the Windows clean-run work, not by this script.
- A first `dotnet publish` right after a fresh SDK install failed once with no output; the script retries once.

Reference log from a real clean run: `docs/audit/install-clean-run-2026-11-22.txt`. The same script runs on a hosted
`ubuntu-latest` runner in the `install-clean-linux` workflow (manual and nightly).
