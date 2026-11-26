-- Binds a crash report's random install id to the authenticated device that first presented it
-- (docs/design/32 "Crash install binding" [PROPOSED], docs/design/43 section 12.1 step 1).
-- device_id is not a foreign key: the device table belongs to the remote namespace and revoked devices must stay listed for erasure.
CREATE TABLE crash_install (
    install_id    uuid        PRIMARY KEY,
    device_id     uuid        NOT NULL,
    shared        boolean     NOT NULL DEFAULT false,
    registered_at timestamptz NOT NULL
);
CREATE INDEX crash_install_device_idx ON crash_install (device_id);

GRANT SELECT, INSERT ON crash_install TO bazaar_app;
