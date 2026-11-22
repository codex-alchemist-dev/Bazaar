-- Roles with floors (design doc 48 section 4, DECIDED). A role is a named set of grants; a grant is a capability over a scope; a
-- role is held by a principal (human, agent or service). The five built-in templates are ordinary rows with template_key set, so
-- the owner can edit them. The owner template is protected (see doc 48 section 4.1).
-- Migration namespace: roles/. Numbering is local to this namespace, so the first file is 0001.
-- No foreign keys to person or agent tables: principal ids are opaque uuids owned by the identity layer, and principal_kind is
-- stored here so the floors (agents never hold admin.* or approve) can be checked without asking identity.
-- The floors that need two rows (the last human owner, an agent holding admin.*) are enforced by RoleService and by the atomic
-- store calls, because a CHECK cannot see other rows. Role changes are audited through the shared audit log, not here.

CREATE TABLE role (
    role_id      uuid        PRIMARY KEY,
    name         text        NOT NULL CHECK (btrim(name) <> ''),
    description  text        NOT NULL DEFAULT '',
    template_key text        NULL CHECK (template_key IN ('owner', 'coworker', 'reviewer', 'read-only-observer', 'agent')),
    protected    boolean     NOT NULL DEFAULT false,
    created_at   timestamptz NOT NULL,
    updated_at   timestamptz NOT NULL
);
-- Names are unique without regard to case, and each template exists at most once.
CREATE UNIQUE INDEX role_name_unique ON role (lower(name));
CREATE UNIQUE INDEX role_template_unique ON role (template_key) WHERE template_key IS NOT NULL;

CREATE TABLE role_grant (
    role_id    uuid NOT NULL REFERENCES role (role_id) ON DELETE CASCADE,
    capability text NOT NULL CHECK (capability ~ '^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$'),
    scope_kind text NOT NULL CHECK (scope_kind IN ('instance', 'company', 'gc', 'gc-tag', 'module', 'repository', 'agent-group')),
    scope_id   text NOT NULL DEFAULT '',
    PRIMARY KEY (role_id, capability, scope_kind, scope_id),
    -- Instance and company scopes carry no id; every other scope names one.
    CHECK ((scope_kind IN ('instance', 'company')) = (scope_id = ''))
);

CREATE TABLE role_assignment (
    principal_id   uuid        NOT NULL,
    role_id        uuid        NOT NULL REFERENCES role (role_id) ON DELETE CASCADE,
    principal_kind text        NOT NULL CHECK (principal_kind IN ('human', 'agent', 'service')),
    assigned_by    uuid        NOT NULL,
    assigned_at    timestamptz NOT NULL,
    PRIMARY KEY (principal_id, role_id)
);
CREATE INDEX role_assignment_role_idx ON role_assignment (role_id, principal_kind);

GRANT SELECT, INSERT, UPDATE, DELETE ON role, role_grant, role_assignment TO bazaar_app;
