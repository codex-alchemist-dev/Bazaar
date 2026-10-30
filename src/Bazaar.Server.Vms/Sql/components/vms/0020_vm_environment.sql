-- VM environment tables (docs/design/16-agent-vms-and-environments.md, data and interfaces). Owner: VM1 (component vms).
-- Migration number 0020 (renumbered from 0019, which other open branches already claim). Conventions as in 0010: text
-- enums with CHECK, RESTRICT foreign keys. Ids come from the caller (UUIDv7 in code), so no server-side UUID function is needed.
-- agent_id carries NO foreign key: the agent table belongs to the agent runtime component (0010), and this component
-- depends on contracts only. The caller guarantees the link.

CREATE TABLE golden_image (
    id                  uuid        PRIMARY KEY,
    os                  text        NOT NULL CHECK (os IN ('windows', 'linux')),
    version             integer     NOT NULL CHECK (version >= 1),
    sha256              text        NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    guest_agent_version text        NOT NULL CHECK (length(guest_agent_version) BETWEEN 1 AND 64),
    state               text        NOT NULL CHECK (state IN ('Building', 'Ready', 'Retired')),
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT golden_image_os_version_key UNIQUE (os, version)
);

CREATE TABLE environment_vm (
    id              uuid        PRIMARY KEY,
    -- No foreign key: agent_id references the agent table owned by migration 0010 (see header).
    agent_id        uuid        NOT NULL,
    os              text        NOT NULL CHECK (os IN ('windows', 'linux')),
    parent_image_id uuid        NOT NULL REFERENCES golden_image (id) ON DELETE RESTRICT,
    backend_ref     text        CHECK (backend_ref IS NULL OR length(backend_ref) BETWEEN 1 AND 256),
    state           text        NOT NULL CHECK (state IN ('Absent', 'Provisioning', 'Stopped', 'Running', 'Saved',
                                                          'Checkpointed', 'Restored', 'Retired')),
    vcpu            integer     NOT NULL CHECK (vcpu >= 1),
    memory_mb       integer     NOT NULL CHECK (memory_mb >= 1),
    disk_cap_gb     integer     NOT NULL CHECK (disk_cap_gb >= 1),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- One live (non-retired) VM per agent and OS. Retiring a VM frees the slot for a new one.
CREATE UNIQUE INDEX environment_vm_live_agent_os_idx ON environment_vm (agent_id, os) WHERE state <> 'Retired';
CREATE INDEX environment_vm_agent_idx ON environment_vm (agent_id, created_at, id);
CREATE INDEX environment_vm_live_idx ON environment_vm (created_at, id) WHERE state <> 'Retired';

CREATE TABLE environment_checkpoint (
    id          uuid        PRIMARY KEY,
    vm_id       uuid        NOT NULL REFERENCES environment_vm (id) ON DELETE RESTRICT,
    backend_ref text        NOT NULL CHECK (length(backend_ref) BETWEEN 1 AND 256),
    label       text        CHECK (label IS NULL OR length(label) BETWEEN 1 AND 128),
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX environment_checkpoint_vm_idx ON environment_checkpoint (vm_id, created_at, id);

CREATE TABLE scoped_mount (
    id          uuid        PRIMARY KEY,
    -- No foreign key: agent_id references the agent table owned by migration 0010 (see header).
    agent_id    uuid        NOT NULL,
    vm_id       uuid        NOT NULL REFERENCES environment_vm (id) ON DELETE RESTRICT,
    kind        text        NOT NULL CHECK (kind IN ('soul', 'workspace')),
    host_path   text        NOT NULL CHECK (length(host_path) BETWEEN 1 AND 4096),
    guest_path  text        NOT NULL CHECK (length(guest_path) BETWEEN 1 AND 4096),
    mode        text        NOT NULL CHECK (mode IN ('ro', 'rw')),
    detached_at timestamptz
);

CREATE INDEX scoped_mount_vm_idx ON scoped_mount (vm_id, id);
CREATE INDEX scoped_mount_agent_idx ON scoped_mount (agent_id);
-- A guest path carries at most one attached mount at a time.
CREATE UNIQUE INDEX scoped_mount_attached_guest_path_idx ON scoped_mount (vm_id, guest_path) WHERE detached_at IS NULL;

CREATE TABLE exec_log (
    id          bigserial   PRIMARY KEY,
    vm_id       uuid        NOT NULL REFERENCES environment_vm (id) ON DELETE RESTRICT,
    started_at  timestamptz NOT NULL,
    argv_hash   text        NOT NULL CHECK (argv_hash ~ '^[0-9a-f]{64}$'),
    timeout_s   integer     NOT NULL CHECK (timeout_s > 0),
    -- Exit code and output size are written together when the exec completes. Exec metadata only (doc 16);
    -- security audit goes to audit_log.
    exit_code   integer,
    bytes_out   bigint      CHECK (bytes_out IS NULL OR bytes_out >= 0),
    CONSTRAINT exec_log_complete_pair CHECK ((exit_code IS NULL) = (bytes_out IS NULL))
);

CREATE INDEX exec_log_vm_started_idx ON exec_log (vm_id, started_at);
