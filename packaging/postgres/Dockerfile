# Linux packaged PostgreSQL for Compose: PostgreSQL 18 plus pgvector from the PGDG repository.
# The Linux packaged install (managed mode) uses the same major and pgvector version as versions.txt.
# The base image is pinned by digest in the Compose file; bump both together.
ARG PG_MAJOR=18
FROM postgres:${PG_MAJOR}-bookworm
ARG PG_MAJOR=18
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-${PG_MAJOR}-pgvector \
 && rm -rf /var/lib/apt/lists/*
# The migrations create the extension; the image only has to make it available.
