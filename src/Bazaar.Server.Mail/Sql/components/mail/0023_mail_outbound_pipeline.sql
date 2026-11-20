-- Outbound pipeline (docs/design/22-email-subsystem.md, "Outbound pipeline (WP6)", durable outbound store option (a)).
-- Additive: widens two CHECK lists and adds nullable columns; 0021 and 0022 are untouched and existing rows stay valid.
-- A message row is stored in state 'sending' before the back end call, then mail_outbound records the receipt, so an idempotency key survives a restart.

ALTER TABLE mail_message DROP CONSTRAINT mail_message_state_check;
ALTER TABLE mail_message
    ADD CONSTRAINT mail_message_state_check CHECK (state IN ('received', 'held', 'released', 'delivered', 'purged', 'reported', 'queued', 'sending', 'sent', 'failed'));

-- awaiting_approval is Tier 2 held for the owner; rejected and expired are the two refusals of an approval (terminal, so a repeated key reports them).
ALTER TABLE mail_outbound DROP CONSTRAINT mail_outbound_status_check;
ALTER TABLE mail_outbound
    ADD CONSTRAINT mail_outbound_status_check CHECK (status IN ('awaiting_approval', 'pending', 'sending', 'sent', 'failed', 'bounced', 'rejected', 'expired'));

-- The policy's answer is kept with the send so a resumed send uses the footer the policy resolved and never a recomputed value.
-- None of these is personal data; the recipients, subject and text stay in the encrypted body columns of mail_message.
ALTER TABLE mail_outbound
    ADD COLUMN tier   smallint NULL CHECK (tier IS NULL OR tier IN (1, 2)),
    ADD COLUMN reason text     NULL,
    ADD COLUMN footer text     NULL;

-- The resumer looks for sends that need attention by status and due time.
CREATE INDEX mail_outbound_due_idx ON mail_outbound (status, next_attempt_at, updated_at)
    WHERE status IN ('awaiting_approval', 'pending', 'sending');
