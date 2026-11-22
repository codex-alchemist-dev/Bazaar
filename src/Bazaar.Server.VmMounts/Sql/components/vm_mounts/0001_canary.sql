-- Planted canary fingerprints for scoped mounts (docs/design/16-agent-vms-and-environments.md WP8). Owner: component vm-mounts
-- (migration namespace vm_mounts). The component depends on vms: scoped_mount belongs to it, so the foreign key below is allowed.
-- Only the SHA-256 fingerprint of a canary value is stored, never the value. Rows are retired when the mount detaches and never
-- deleted: a canary value that turns up after the detach is still evidence of a leak.

CREATE TABLE vm_mount_canary (
    id            uuid        PRIMARY KEY,
    mount_id      uuid        NOT NULL REFERENCES scoped_mount (id) ON DELETE RESTRICT,
    -- No foreign keys on agent_id and vm_id, as in scoped_mount (the agent table is owned by another component).
    agent_id      uuid        NOT NULL,
    vm_id         uuid        NOT NULL,
    relative_path text        NOT NULL CHECK (length(relative_path) BETWEEN 1 AND 512),
    fingerprint   text        NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    planted_at    timestamptz NOT NULL,
    retired_at    timestamptz
);

CREATE UNIQUE INDEX vm_mount_canary_fingerprint_idx ON vm_mount_canary (fingerprint);
CREATE INDEX vm_mount_canary_mount_idx ON vm_mount_canary (mount_id, id);
CREATE INDEX vm_mount_canary_active_idx ON vm_mount_canary (mount_id) WHERE retired_at IS NULL;
