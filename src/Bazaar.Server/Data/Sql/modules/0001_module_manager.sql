-- Module manager persistence (doc 42): update decisions and line-move audit events.
-- Migration namespace: modules/ (not common/, which is frozen). Numbering is local to this namespace, so the first file is 0001.
-- No foreign keys: module ids and move ids are opaque text keys owned by the module manager, not by a table in another namespace.
--
-- Both tables are append-only. bazaar_app gets INSERT and SELECT only, and a trigger refuses UPDATE, DELETE and TRUNCATE for
-- everyone (the owner and superusers included) unless a deliberate DISABLE TRIGGER lets a change through.
--
-- The CHECK lists are the C# enum names: UpdateDecision (Modules/Updates/UpdatePolicyModels.cs), LineMoveStep and
-- LineMoveAuditKind (Modules/LineMove). A test keeps them in step with the enums.

CREATE TABLE module_update_decision (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    module_id   text        NOT NULL CHECK (btrim(module_id) <> ''),
    version     text        NOT NULL CHECK (btrim(version) <> ''),
    line        text        NOT NULL CHECK (btrim(line) <> ''),
    decision    text        NOT NULL CHECK (decision IN (
                    'Auto', 'Manual', 'Deferred', 'Skipped', 'Pinned', 'AwaitingApproval', 'Blocked')),
    reason      text        NOT NULL,
    policy_id   text        NULL CHECK (policy_id IS NULL OR btrim(policy_id) <> ''),
    decided_at  timestamptz NOT NULL,
    actor       text        NOT NULL CHECK (btrim(actor) <> '')
);
CREATE INDEX module_update_decision_module_idx ON module_update_decision (module_id, decided_at);

CREATE TABLE module_line_move_event (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    move_id         text        NOT NULL CHECK (btrim(move_id) <> ''),
    step            text        NOT NULL CHECK (step IN (
                        'Preflight', 'Snapshot', 'DryRun', 'Switch', 'Verify', 'Complete', 'RolledBack')),
    outcome         text        NOT NULL CHECK (outcome IN (
                        'Succeeded', 'Failed', 'Cancelled', 'ManualRollbackRequired')),
    from_line       text        NOT NULL CHECK (btrim(from_line) <> ''),
    to_line         text        NOT NULL CHECK (btrim(to_line) <> ''),
    snapshot_id     text        NULL CHECK (snapshot_id IS NULL OR btrim(snapshot_id) <> ''),
    approving_role  text        NOT NULL CHECK (btrim(approving_role) <> ''),
    occurred_at     timestamptz NOT NULL,
    detail          text        NOT NULL
);
CREATE INDEX module_line_move_event_move_idx ON module_line_move_event (move_id, occurred_at);

-- Append-only guard. One function serves both tables; the error names the table.
CREATE FUNCTION module_manager_refuse_change() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'module manager record is append-only: % refused on %', TG_OP, TG_TABLE_NAME;
END;
$$;

CREATE TRIGGER module_update_decision_no_update_or_delete BEFORE UPDATE OR DELETE ON module_update_decision
    FOR EACH ROW EXECUTE FUNCTION module_manager_refuse_change();
CREATE TRIGGER module_update_decision_no_truncate BEFORE TRUNCATE ON module_update_decision
    FOR EACH STATEMENT EXECUTE FUNCTION module_manager_refuse_change();
CREATE TRIGGER module_line_move_event_no_update_or_delete BEFORE UPDATE OR DELETE ON module_line_move_event
    FOR EACH ROW EXECUTE FUNCTION module_manager_refuse_change();
CREATE TRIGGER module_line_move_event_no_truncate BEFORE TRUNCATE ON module_line_move_event
    FOR EACH STATEMENT EXECUTE FUNCTION module_manager_refuse_change();

GRANT SELECT, INSERT ON module_update_decision, module_line_move_event TO bazaar_app;
-- An identity column needs USAGE on its sequence for an INSERT by bazaar_app.
GRANT USAGE ON SEQUENCE module_update_decision_id_seq, module_line_move_event_id_seq TO bazaar_app;
