# Bazaar Compose: PostgreSQL with pgvector (Linux)

One Compose stack that runs PostgreSQL with pgvector, bound to loopback. It is the Linux route to a
packaged database for development and self-hosting. The Bazaar host runs outside this stack in
**external mode** (`Bazaar:Database:Mode`, see `docs/research/notes/Bazaar server packaging/packaging.md`).

## What it does and does not do

- **Does:** runs `postgres` from the official pgvector image (PostgreSQL 19 by default, 18 with an override),
  creates the database `bazaar` and the role `bazaar`, keeps data in a named volume, reports health with
  `pg_isready`, and maps the port to `127.0.0.1:5432` only.
- **Does not:** run the Bazaar host. The stack has no Bazaar container yet. The host is started separately and
  points at this database through `BAZAAR_DB`.
- **Does not:** create the schema. The host's `migrate` command does that, and it runs the pgvector
  `CREATE EXTENSION` as the superuser role, which this image gives to `bazaar`.
- **Does not:** expose the database to the network. Only the local machine can connect.

## Run it

1. Copy the example file and set a password:

       cd deploy/compose
       cp .env.example .env
       # edit .env and set BAZAAR_DB_PASSWORD (for example: openssl rand -hex 24)

2. Start the database:

       docker compose up -d
       docker compose ps            # wait until postgres shows "healthy"

   If `BAZAAR_DB_PASSWORD` is empty or missing, compose stops before starting and prints the error.

3. Start the Bazaar host against it (the host is not in this stack yet):

       export BAZAAR_DB='Host=127.0.0.1;Port=5432;Database=bazaar;Username=bazaar;Password=<the password from .env>'

   Use the port from `BAZAAR_DB_PORT` if you changed it. Then run the host or `migrate` as in `scripts/dev/README.md`.

4. Stop it with `docker compose down`. Data stays in the volume. `docker compose down -v` deletes the data.

## Switch to PostgreSQL 18

PostgreSQL 18 is still supported. Use the override file with both files on the command line:

    docker compose -f compose.yaml -f compose.pg18.yaml up -d

This uses the `pgvector/pgvector:pg18` image and its own volume (`bazaar-pgdata-pg18`). The PG 19 volume is not
touched, and PostgreSQL does not read data from another major, so switch by pointing at the matching volume.
Changing the major on an existing volume needs a dump and restore; it is not an in-place switch.

If you only want 18 without the override, set `PG_MAJOR=18` in `.env`.

## Known limits

- **PostgreSQL 19 image:** the default `pgvector/pgvector:pg19` tag was not published on Docker Hub when this was
  written (checked 2026-10-30, HTTP 404). `docker compose up` fails to pull it until the tag exists. Use the 18
  override until then.
- **Not run end to end:** this folder was checked for YAML syntax only. It has not been started in a container
  here. Run it on the target Linux host before relying on it.

## Windows

Docker on Windows needs **Docker Desktop with the WSL 2 or Hyper-V backend**. The same compose files work, but
the Linux container path is not the Windows route to a packaged database.

On Windows, use **managed mode** (`Bazaar:Database:Mode=managed`, packaging doc section 3). That mode starts a
private PostgreSQL from the bundled binaries as a child of the Bazaar host, with no Docker needed.
