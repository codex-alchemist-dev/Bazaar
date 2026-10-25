-- 0004: audit log (doc 15 "Audit log and kill switch", doc 04 conventions). Owner: A3.
--
-- Rows are the hash chain that AuditChain (Security/Audit) computes; PostgresAuditLog (Security/Audit/Db) is the
-- only writer. Column choices against doc 15's shape:
--   seq        bigint PRIMARY KEY, not bigserial. The chain needs seq = head + 1 with no gaps. A sequence skips
--              values when a transaction rolls back, and AuditChain.Verify would then fail. The writer assigns seq
--              under an advisory lock.
--   ts         timestamptz (UTC). The writer truncates to microseconds, the precision Postgres stores, before hashing.
--   payload    jsonb. The writer hashes the jsonb canonical text, which is what a read returns (payload::text).
--   prev_hash, hash   bytea, SHA-256 (32 bytes). The first row's prev_hash is 32 zero bytes (AuditChain.GenesisHash).
--
-- Roles (doc 04: a migration role for DDL and a runtime role for DML with no UPDATE or DELETE on log tables).
-- The runtime role is bazaar_app. Doc 04 does not name it, so it is chosen here; 0001 may create it first, and this
-- file creates it only when it is missing. It is NOLOGIN: deployments grant it to the login role the server connects as.
-- The migration role owns the table; it is not granted DML.

CREATE TABLE audit_log (
    seq       bigint      PRIMARY KEY CHECK (seq >= 1),
    ts        timestamptz NOT NULL,
    actor     text        NOT NULL,
    kind      text        NOT NULL,
    payload   jsonb       NOT NULL,
    prev_hash bytea       NOT NULL CHECK (octet_length(prev_hash) = 32),
    hash      bytea       NOT NULL CHECK (octet_length(hash) = 32)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bazaar_app') THEN
        CREATE ROLE bazaar_app NOLOGIN;
    END IF;
EXCEPTION WHEN duplicate_object OR unique_violation THEN
    NULL; -- a concurrent run created it first
END
$$;

REVOKE ALL ON audit_log FROM PUBLIC;
REVOKE ALL ON audit_log FROM bazaar_app;
GRANT INSERT, SELECT ON audit_log TO bazaar_app;

-- Defence in depth. Privileges already refuse UPDATE, DELETE and TRUNCATE for bazaar_app (SQLSTATE 42501). These
-- triggers also refuse the table owner and superusers, who bypass privileges but not triggers. Only a deliberate
-- DISABLE TRIGGER (a DDL-level act, which the audit log records elsewhere) lets a change through.
CREATE FUNCTION audit_log_refuse_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only: % refused', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END
$$;

CREATE TRIGGER audit_log_no_update_or_delete
    BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_refuse_change();

CREATE TRIGGER audit_log_no_truncate
    BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_refuse_change();
