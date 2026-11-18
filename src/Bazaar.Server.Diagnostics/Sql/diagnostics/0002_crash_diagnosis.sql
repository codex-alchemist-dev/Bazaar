-- Server-side crash diagnosis (docs/design/43-telemetry-and-crash-reporting.md section 9.1).
-- The label is a constant the database enforces as well as the writer. A diagnosis made from a report is removed with that report
-- (retention and erasure), so no text derived from a deleted report outlives it. A diagnosis with no report stays with its group.

CREATE TABLE crash_diagnosis (
    diagnosis_id   uuid        PRIMARY KEY,
    fingerprint    text        NOT NULL REFERENCES crash_group (fingerprint) ON DELETE CASCADE,
    report_id      uuid        NULL REFERENCES crash_report (report_id) ON DELETE CASCADE,
    kind           text        NOT NULL DEFAULT 'server' CHECK (kind IN ('server', 'device')),
    label          text        NOT NULL DEFAULT 'unverified diagnosis' CHECK (label = 'unverified diagnosis'),
    produced_at    timestamptz NOT NULL,
    role_model     text        NOT NULL CHECK (btrim(role_model) <> ''),
    input_hash     bytea       NOT NULL CHECK (octet_length(input_hash) = 32),
    text_out       text        NOT NULL,
    posted_to_issue boolean    NOT NULL DEFAULT false
);
CREATE INDEX crash_diagnosis_group_idx ON crash_diagnosis (fingerprint, produced_at DESC);

GRANT SELECT, INSERT, UPDATE, DELETE ON crash_diagnosis TO bazaar_app;
