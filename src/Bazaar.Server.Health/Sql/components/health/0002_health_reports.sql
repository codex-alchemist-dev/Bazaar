-- Raw health reports (docs/design/25-company-systems-knowledge.md, Health and Analytics; setting metrics.raw_retention_days).
-- Component migration 0002 in the health namespace. A CI run or an agent pushes a coverage, analyzer or package-audit report;
-- the collection job derives metric_samples from them. Samples are recomputable and stay; raw reports are pruned by day.
-- Identity is (project, kind, toolchain, day, source_ref), so a replayed push writes the same row.
CREATE TABLE health_reports (
    project_id  text        NOT NULL CHECK (btrim(project_id) <> ''),
    kind        text        NOT NULL CHECK (kind IN ('coverage', 'analyzer', 'package_audit')),
    toolchain   text        NOT NULL CHECK (btrim(toolchain) <> ''),
    day         date        NOT NULL,
    source_ref  text        NOT NULL CHECK (btrim(source_ref) <> ''),
    content     text        NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, kind, toolchain, day, source_ref)
);

CREATE INDEX health_reports_day ON health_reports (day);
