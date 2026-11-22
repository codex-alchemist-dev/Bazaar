-- Metric samples (docs/design/25-company-systems-knowledge.md, Health and Analytics row and WP1).
-- Component migration 0001 in the health namespace. Project values are slugs owned by other components, so no foreign keys.
-- One row per derived measurement. Identity is (project, metric, dimension, bucket, source_ref), so a replayed source writes the same
-- row again. Rows exist only for days that have a source: a missing CI run is a gap, never a zero. Recomputable from the sources.
CREATE TABLE metric_samples (
    project_id  text             NOT NULL CHECK (btrim(project_id) <> ''),
    metric      text             NOT NULL CHECK (btrim(metric) <> ''),
    dimension   text             NOT NULL DEFAULT '',
    bucket      date             NOT NULL,
    value       double precision NOT NULL,
    source_ref  text             NOT NULL CHECK (btrim(source_ref) <> ''),
    recorded_at timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, metric, dimension, bucket, source_ref)
);

CREATE INDEX metric_samples_lookup ON metric_samples (project_id, metric, bucket);
