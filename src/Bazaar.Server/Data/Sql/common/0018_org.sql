-- Org relations (docs/design/24 section 2, owner WP ORG1). One row per relation edge between two agents.
-- Kinds: reports_to (approval routing and escalation only) and lent_to (a project borrows an agent). The kinds are a
-- CHECK constraint, so adding one is a schema change (doc 24 Q2). No foreign keys to agent, project or principal
-- tables: those keys arrive with the cross-table migration (0005, reserved). Ids are supplied by the writer, as in 0015.
-- Org edges grant nothing (doc 24: access comes from grants and the Identity Service).

CREATE TABLE org_relation (
    id            uuid        PRIMARY KEY,
    kind          text        NOT NULL CHECK (kind IN ('reports_to', 'lent_to')),
    -- Subject and object: for reports_to, from_agent reports to to_agent; for lent_to, from_agent is lent to to_agent.
    from_agent_id uuid        NOT NULL,
    to_agent_id   uuid        NOT NULL,
    -- NULL = company-wide. A loan always belongs to a project (doc 24, Loans).
    project_id    uuid        NULL,
    starts_at     timestamptz NOT NULL,
    ends_at       timestamptz NULL,
    created_by    uuid        NOT NULL,
    version       bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    CONSTRAINT org_relation_no_self CHECK (from_agent_id <> to_agent_id),
    CONSTRAINT org_relation_span CHECK (ends_at IS NULL OR ends_at >= starts_at),
    CONSTRAINT org_relation_loan_has_project CHECK (kind <> 'lent_to' OR project_id IS NOT NULL)
);

-- One live edge per (kind, subject, object, scope). Ended rows are history and may repeat an edge. NULLS NOT DISTINCT
-- makes two company-wide rows (project_id NULL) collide.
CREATE UNIQUE INDEX org_relation_live_edge_idx ON org_relation (kind, from_agent_id, to_agent_id, project_id) NULLS NOT DISTINCT
    WHERE ends_at IS NULL;

-- One live reports_to per subject and scope (doc 24: "one reports_to per project scope").
CREATE UNIQUE INDEX org_relation_one_reports_to_idx ON org_relation (from_agent_id, project_id) NULLS NOT DISTINCT
    WHERE kind = 'reports_to' AND ends_at IS NULL;

CREATE INDEX org_relation_from_idx ON org_relation (from_agent_id, kind, project_id) WHERE ends_at IS NULL;
CREATE INDEX org_relation_to_idx ON org_relation (to_agent_id, kind, project_id) WHERE ends_at IS NULL;

-- Write rules that CHECK and UNIQUE cannot express:
--   * identity fields never change after insert; an ended row is read-only (doc 24, edge cases); ending is the only update;
--   * a live reports_to may not close a cycle within its scope. The walk runs under an advisory lock per scope, so two
--     writers cannot each miss the other's edge. Relies on READ COMMITTED (each statement sees earlier commits).
-- Cycles are checked on insert only: an update can only end a row, and ending a reports_to cannot create a cycle.
CREATE FUNCTION org_relation_write_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    closes_cycle boolean;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.ends_at IS NOT NULL THEN
            RAISE EXCEPTION 'org_relation % has ended and is read-only', OLD.id USING ERRCODE = 'check_violation';
        END IF;
        IF (NEW.id, NEW.kind, NEW.from_agent_id, NEW.to_agent_id, NEW.project_id, NEW.starts_at, NEW.created_by)
           IS DISTINCT FROM (OLD.id, OLD.kind, OLD.from_agent_id, OLD.to_agent_id, OLD.project_id, OLD.starts_at, OLD.created_by) THEN
            RAISE EXCEPTION 'org_relation % identity fields are fixed after insert', OLD.id USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.kind = 'reports_to' AND NEW.ends_at IS NULL THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('org.reports_to:' || coalesce(NEW.project_id::text, 'company'), 0));
        -- Walk up from the new manager through live reports_to edges of the same scope. UNION (not UNION ALL) stops on
        -- any loop that already exists in the data.
        WITH RECURSIVE up(agent_id) AS (
            SELECT NEW.to_agent_id
            UNION
            SELECT r.to_agent_id
            FROM up
            JOIN org_relation r ON r.from_agent_id = up.agent_id
            WHERE r.kind = 'reports_to' AND r.ends_at IS NULL AND r.project_id IS NOT DISTINCT FROM NEW.project_id
        )
        SELECT EXISTS (SELECT 1 FROM up WHERE up.agent_id = NEW.from_agent_id) INTO closes_cycle;
        IF closes_cycle THEN
            RAISE EXCEPTION 'org.reports_to cycle: % would report to % through its own chain', NEW.from_agent_id, NEW.to_agent_id
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER org_relation_write_check BEFORE INSERT OR UPDATE ON org_relation
    FOR EACH ROW EXECUTE FUNCTION org_relation_write_check();

-- Runtime role: insert and read, and end a row. No DELETE. Roles are cluster-wide, so create only if missing (as in 0001).
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- another database on the cluster created it concurrently
END
$$;

GRANT SELECT, INSERT ON org_relation TO bazaar_app;
GRANT UPDATE (ends_at, version) ON org_relation TO bazaar_app;
