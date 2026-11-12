# Linux packaged PostgreSQL for Compose: PostgreSQL 18 (default) or 19 plus pgvector from the PGDG repository.
# The Linux packaged install (managed mode) uses the same major and pgvector version as versions.txt.
# The base image is pinned by digest in the Compose file; bump both together.
ARG PG_MAJOR=18
# The base image defaults to the official tag for the major. A release candidate uses its own tag
# (for example postgres:19rc1-bookworm) through PG_BASE, so the same Dockerfile builds 18 and 19.
ARG PG_BASE=postgres:${PG_MAJOR}-bookworm
FROM ${PG_BASE}
ARG PG_MAJOR=18
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-${PG_MAJOR}-pgvector \
 && rm -rf /var/lib/apt/lists/*
# The migrations create the extension; the image only has to make it available.
