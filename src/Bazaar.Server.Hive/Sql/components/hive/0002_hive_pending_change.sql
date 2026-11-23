-- Hive grant changes waiting for the owner's decision (doc 25, "Hive grant wire request"). One row per approval ticket; the row is
-- deleted once the change is applied or the approval is denied, expired or gone. Numbering is local to the hive namespace.
-- ticket_id is the approval id; project values are slugs owned by other components, so no foreign keys.
CREATE TABLE hive_pending_change (
    ticket_id    uuid        PRIMARY KEY,
    action       text        NOT NULL CHECK (action IN ('set', 'revoke')),
    from_project text        NOT NULL CHECK (btrim(from_project) <> ''),
    to_project   text        NOT NULL CHECK (btrim(to_project) <> ''),
    kind         text        NOT NULL CHECK (kind IN ('procedure', 'failure', 'technical-fact')),
    created_at   timestamptz NOT NULL DEFAULT now()
);
