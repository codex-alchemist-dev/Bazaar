# Known limits of the source-compile pipeline

Status as of 2026-11-16. Doc 42 section 14.1 holds the design; this file lists what the pipeline cannot do yet.

## PostgreSQL 19 with pgvector 0.8.2 (blocked upstream)

- The PostgreSQL 19 release candidate (19rc2) does not build pgvector 0.8.2 on Linux. The pgvector build fails against the 19 headers.
- On Windows, the 19 build needs `/std:c11` for the pgvector compile, because the PostgreSQL 19 headers use C11 keywords (`_Noreturn`, `alignas`). The script sets it. With that flag the header errors are gone, but pgvector 0.8.2's `src/hnsw.h` still fails to compile against the PostgreSQL 19 headers (undefined `LWLock`, `slock_t`, `Page`), followed by an MSVC internal compiler error. Same upstream block as Linux.
- Both PostgreSQL 19 jobs in `postgres-source-build.yml` are `continue-on-error` and informational. A passing 19 job is the gate before any 19 bundle ships.
- Unblock: a pgvector release that builds against PostgreSQL 19 GA, then re-pin `PGVECTOR_VERSION` and its SHA-256 in `sources.txt`.

## Signing

- Bundles are written with `signed=false`. The `sign-linux-18` job signs only on `workflow_dispatch`, under the `release` environment, using the release key secret. Signing is not part of the normal PR run.

## Windows source build

- Uses `-Dssl=none -Dzlib=disabled`, because no OpenSSL or zlib source is pinned for Windows yet. The host connects over loopback with scram-sha-256. Doc 42 section 14.1 records the difference from Linux.
