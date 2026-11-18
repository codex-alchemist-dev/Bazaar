-- Module permission approvals (doc 48 section 1, doc 50 section 5): one row per owner approval of a module version's
-- permission set. Lives in the modules namespace with 0001 (common/ is frozen). No foreign keys: module ids and versions are
-- opaque text keys owned by the module manager.
--
-- Append-only, like module_update_decision: bazaar_app gets INSERT and SELECT only, and the refuse trigger from 0001 blocks
-- UPDATE, DELETE and TRUNCATE for everyone. A row is never written without the owner's signature over the approval form
-- (ModuleApprovalForm). The approval row and its module.permissions.approved audit row commit in one transaction.

CREATE TABLE module_permission_approval (
    id                      bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    module_id               text        NOT NULL CHECK (btrim(module_id) <> ''),
    version                 text        NOT NULL CHECK (btrim(version) <> ''),
    permissions             text[]      NOT NULL CHECK (cardinality(permissions) > 0),
    approval_digest_sha256  text        NOT NULL CHECK (approval_digest_sha256 ~ '^[0-9a-f]{64}$'),
    signer_key_id           text        NOT NULL CHECK (btrim(signer_key_id) <> ''),
    signature               bytea       NOT NULL CHECK (octet_length(signature) > 0),
    owner_actor             text        NOT NULL CHECK (btrim(owner_actor) <> ''),
    approved_at             timestamptz NOT NULL
);
CREATE INDEX module_permission_approval_version_idx ON module_permission_approval (module_id, version, approved_at);

CREATE TRIGGER module_permission_approval_no_update_or_delete BEFORE UPDATE OR DELETE ON module_permission_approval
    FOR EACH ROW EXECUTE FUNCTION module_manager_refuse_change();
CREATE TRIGGER module_permission_approval_no_truncate BEFORE TRUNCATE ON module_permission_approval
    FOR EACH STATEMENT EXECUTE FUNCTION module_manager_refuse_change();

GRANT SELECT, INSERT ON module_permission_approval TO bazaar_app;
-- An identity column needs USAGE on its sequence for an INSERT by bazaar_app.
GRANT USAGE ON SEQUENCE module_permission_approval_id_seq TO bazaar_app;
