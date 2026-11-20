-- Mailbox to person (docs/design/22-email-subsystem.md, "Mailbox directory"). Additive: one nullable column; 0021 to 0023 are untouched and
-- existing rows stay valid. person_id is the agent's principal id (the same GUID the GC body codec and person_key use). A mailbox with
-- NULL person_id is not pulled by the inbound worker. The inbox GC is not stored: it is read from the GC tables (type inbox, person a member).
-- No foreign key, as for soul_id: the agent tables belong to other modules.

ALTER TABLE mail_mailbox ADD COLUMN person_id uuid NULL;

CREATE INDEX mail_mailbox_person_idx ON mail_mailbox (person_id) WHERE person_id IS NOT NULL;
