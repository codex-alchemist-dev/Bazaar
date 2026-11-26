-- Approved recipients (docs/design/22-email-subsystem.md, "Approved recipients source"). Additive: one new table; 0021 to 0024 are untouched.
-- An operator-managed allow list of addresses and domains that outbound mail may go to without a Tier 2 approval. The table starts empty, so
-- nothing is approved until the approval queue decision path (not built) adds a row. Values are stored lower case; no domain is shipped.
-- No foreign keys: approval_id names an approval owned by another module.

CREATE TABLE mail_approved_recipient (
    id                 uuid        PRIMARY KEY,
    kind               text        NOT NULL CHECK (kind IN ('address', 'domain')),
    value              text        NOT NULL,
    include_subdomains boolean     NOT NULL DEFAULT false,
    added_at           timestamptz NOT NULL DEFAULT now(),
    added_by           text        NOT NULL,
    approval_id        uuid        NULL,
    CONSTRAINT mail_approved_recipient_lower CHECK (value = lower(value) AND value = btrim(value) AND value <> ''),
    CONSTRAINT mail_approved_recipient_address_form CHECK (kind <> 'address' OR (position('@' IN value) > 1 AND position('@' IN value) < length(value) AND value !~ '\s' AND include_subdomains = false)),
    CONSTRAINT mail_approved_recipient_domain_form CHECK (kind <> 'domain' OR value ~ '^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$')
);

CREATE UNIQUE INDEX mail_approved_recipient_value_idx ON mail_approved_recipient (kind, value);
