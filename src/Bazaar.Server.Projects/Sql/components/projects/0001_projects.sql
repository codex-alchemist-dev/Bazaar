-- Projects and membership (docs/design/93-projects-and-membership.md section 3). Component migration 0001 in the projects
-- namespace (common is frozen). No foreign keys to agent, org_relation or the tables that carry a bare project_id: those
-- modules add theirs in their own change (doc 93 section 3). Actors are text labels ("owner", "agent:<uuid>", "user:<uuid>").
-- A loan is never copied here: the membership source unions project_member with org_relation, so an ended loan stops
-- granting access with no cleanup job.

CREATE TABLE project (
    id         uuid        PRIMARY KEY,
    slug       text        NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,62}$'),
    title      text        NOT NULL CHECK (btrim(title) <> ''),
    state      text        NOT NULL DEFAULT 'active' CHECK (state IN ('active', 'archived')),
    created_by text        NOT NULL CHECK (btrim(created_by) <> ''),
    created_at timestamptz NOT NULL,
    version    bigint      NOT NULL DEFAULT 1 CHECK (version >= 1)
);

-- One row per principal and project. Ending sets ends_at; re-adding resets the row (history lives in the audit log).
CREATE TABLE project_member (
    project_id     uuid        NOT NULL REFERENCES project (id),
    principal_kind text        NOT NULL CHECK (principal_kind IN ('agent', 'user')),
    principal_id   uuid        NOT NULL,
    role           text        NOT NULL CHECK (role IN ('lead', 'member')),
    starts_at      timestamptz NOT NULL,
    ends_at        timestamptz NULL,
    added_by       text        NOT NULL CHECK (btrim(added_by) <> ''),
    version        bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    PRIMARY KEY (project_id, principal_kind, principal_id),
    CONSTRAINT project_member_span CHECK (ends_at IS NULL OR ends_at >= starts_at)
);

CREATE INDEX project_member_principal_idx ON project_member (principal_kind, principal_id);

-- The slug is the Vault path segment, so it never changes; a project that is archived never becomes active again.
CREATE FUNCTION project_write_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.slug IS DISTINCT FROM OLD.slug
       OR NEW.created_by IS DISTINCT FROM OLD.created_by OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'project % identity fields are fixed after insert', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.state = 'archived' AND NEW.state <> 'archived' THEN
        RAISE EXCEPTION 'project % is archived and stays archived', OLD.id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER project_write_check BEFORE UPDATE ON project
    FOR EACH ROW EXECUTE FUNCTION project_write_check();

-- Runtime role: no DELETE on either table. Roles are cluster-wide, so create only if missing (as in common/0001).
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- another database on the cluster created it concurrently
END
$$;

GRANT SELECT, INSERT ON project, project_member TO bazaar_app;
GRANT UPDATE (state, version) ON project TO bazaar_app;
GRANT UPDATE (role, starts_at, ends_at, added_by, version) ON project_member TO bazaar_app;
