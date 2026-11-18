-- Quarantine store columns (docs/design/22-email-subsystem.md, "Quarantine store slice (WP5)", doc gaps). Additive and nullable only:
-- existing mail_message rows stay valid and 0021 is untouched. No plaintext sender address is added: only the sender's domain is kept
-- for the held list and the digest. person_id names the person whose key encrypts the body (legal holds are checked against it).

ALTER TABLE mail_message
    ADD COLUMN person_id          uuid        NULL,
    ADD COLUMN sender_domain      text        NULL,
    ADD COLUMN released_by        uuid        NULL,
    ADD COLUMN released_at        timestamptz NULL,
    ADD COLUMN report_approval_id uuid        NULL,
    ADD COLUMN purged_at          timestamptz NULL;

ALTER TABLE mail_message
    ADD CONSTRAINT mail_message_sender_domain_form CHECK (sender_domain IS NULL OR (sender_domain = lower(sender_domain) AND sender_domain <> '' AND position('@' IN sender_domain) = 0));

-- The owner's pane lists quarantined rows oldest first; only rows that were admitted carry a person id.
CREATE INDEX mail_message_quarantine_idx ON mail_message (received_at, id) WHERE person_id IS NOT NULL;
