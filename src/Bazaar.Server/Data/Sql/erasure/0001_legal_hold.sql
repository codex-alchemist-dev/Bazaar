-- Legal hold events (design doc 48 section 9, DECIDED): "Legal hold blocks removal". While a hold is active, purge and
-- crypto-erase in its scope are blocked; an erasure request inside a hold is queued, not refused.
-- Migration namespace: erasure/ (not common/, which is frozen). Numbering is local to this namespace, so the first file is 0001.
-- No foreign keys: hold ids and scope values are opaque keys owned by the erasure component.
--
-- legal_hold_event is append-only. Each hold has a 'created' event and, when it ends, a 'released' event. Holds are created
-- or released only through the owner's signed path (owner decision 2026-11-16, IOwnerSignatureVerifier, not yet on main).
-- Until then holds can be inserted only by migrations and tests. signature_key_id is filled by that path later.
--
-- bazaar_app gets INSERT and SELECT only. A trigger refuses UPDATE, DELETE and TRUNCATE for everyone (the owner and
-- superusers included) unless a deliberate DISABLE TRIGGER lets a change through.
--
-- Scope kinds 'gc', 'company' and 'category' are stored but NOT enforced yet. Only 'person' holds are read (ILegalHoldReader).
-- How a company or category hold covers a person is an OPEN question for the owner.

CREATE TABLE legal_hold_event (
    event_id          bigserial   PRIMARY KEY,
    hold_id           uuid        NOT NULL,
    action            text        NOT NULL CHECK (action IN ('created', 'released')),
    scope_kind        text        NOT NULL CHECK (scope_kind IN ('person', 'gc', 'company', 'category')),
    scope_value       text        NOT NULL CHECK (btrim(scope_value) <> ''),
    reason            text        NOT NULL CHECK (btrim(reason) <> ''),
    actor             text        NOT NULL CHECK (btrim(actor) <> ''),
    signature_key_id  text        NULL CHECK (signature_key_id IS NULL OR btrim(signature_key_id) <> ''),
    occurred_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX legal_hold_event_scope_idx ON legal_hold_event (scope_kind, scope_value, hold_id);

-- A hold is active when its latest event is 'created' and no 'released' event exists for it.
CREATE VIEW active_legal_hold AS
SELECT c.hold_id, c.scope_kind, c.scope_value, c.reason, c.actor, c.occurred_at AS created_at
FROM legal_hold_event c
WHERE c.action = 'created'
  AND c.event_id = (SELECT max(l.event_id) FROM legal_hold_event l WHERE l.hold_id = c.hold_id)
  AND NOT EXISTS (SELECT 1 FROM legal_hold_event r WHERE r.hold_id = c.hold_id AND r.action = 'released');

-- Append-only guard.
CREATE FUNCTION legal_hold_refuse_change() RETURNS trigger
    LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'legal hold record is append-only: % refused on %', TG_OP, TG_TABLE_NAME;
END;
$$;

CREATE TRIGGER legal_hold_event_no_update_or_delete BEFORE UPDATE OR DELETE ON legal_hold_event
    FOR EACH ROW EXECUTE FUNCTION legal_hold_refuse_change();
CREATE TRIGGER legal_hold_event_no_truncate BEFORE TRUNCATE ON legal_hold_event
    FOR EACH STATEMENT EXECUTE FUNCTION legal_hold_refuse_change();

GRANT SELECT, INSERT ON legal_hold_event TO bazaar_app;
-- An identity-style column needs USAGE on its sequence for an INSERT by bazaar_app.
GRANT USAGE ON SEQUENCE legal_hold_event_event_id_seq TO bazaar_app;
GRANT SELECT ON active_legal_hold TO bazaar_app;
