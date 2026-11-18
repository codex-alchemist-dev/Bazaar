-- Talent Pool store (docs/design/25-company-systems-knowledge.md, Talent Pool row; work package 8).
-- Migration namespace: talent_pool/ (not common/, which is frozen). Numbering is local to this namespace.
-- No foreign keys: source_soul_id is an opaque id owned by the Soul system (07), and this module depends on abstractions only.
--
-- One row per (template, version). The manifest is prompt, tool allowlist and model whitelist only: no memory (CHECK below, and the
-- validator refuses it first). Imports are 'quarantined' until a Council hire motion passes. Rows are never deleted and the content
-- never changes; only a quarantined row's status can move, once, to 'hired' or 'rejected'.

CREATE TABLE talent_templates (
    template_id       uuid        NOT NULL,
    version           integer     NOT NULL CHECK (version >= 1),
    manifest          jsonb       NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
    content_sha       text        NOT NULL CHECK (content_sha ~ '^[0-9a-f]{64}$'),
    status            text        NOT NULL DEFAULT 'quarantined' CHECK (status IN ('quarantined', 'hired', 'rejected')),
    source_soul_id    uuid        NULL,
    submitted_by      text        NOT NULL CHECK (btrim(submitted_by) <> ''),
    created_at        timestamptz NOT NULL DEFAULT now(),
    status_changed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (template_id, version),
    CONSTRAINT talent_templates_one_row_per_content UNIQUE (template_id, content_sha),
    -- Belt and braces: a template carries no memory, whatever the application layer did.
    CONSTRAINT talent_templates_no_memory CHECK (
        NOT (manifest ? 'memory' OR manifest ? 'memories' OR manifest ? 'files' OR manifest ? 'extraFields')
    )
);
CREATE INDEX talent_templates_status_idx ON talent_templates (status, created_at);

CREATE FUNCTION talent_templates_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'talent_templates is append-only: % refused', TG_OP;
    END IF;
    IF NEW.template_id <> OLD.template_id OR NEW.version <> OLD.version OR NEW.manifest <> OLD.manifest
       OR NEW.content_sha <> OLD.content_sha OR NEW.source_soul_id IS DISTINCT FROM OLD.source_soul_id
       OR NEW.submitted_by <> OLD.submitted_by OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'talent_templates content is immutable; only status may change';
    END IF;
    IF OLD.status <> 'quarantined' AND NEW.status <> OLD.status THEN
        RAISE EXCEPTION 'talent_templates status % is final', OLD.status;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER talent_templates_guard_row BEFORE UPDATE OR DELETE ON talent_templates
    FOR EACH ROW EXECUTE FUNCTION talent_templates_guard();
CREATE TRIGGER talent_templates_no_truncate BEFORE TRUNCATE ON talent_templates
    FOR EACH STATEMENT EXECUTE FUNCTION talent_templates_guard();

DO $$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        GRANT SELECT, INSERT ON talent_templates TO bazaar_app;
        GRANT UPDATE (status, status_changed_at) ON talent_templates TO bazaar_app;
    END IF;
END
$$;
