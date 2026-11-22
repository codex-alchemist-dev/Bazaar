-- Token amounts that entered as a digit stream on the HTTP blob channel (doc 48, DECIDED 11.4, "Transport above the frame
-- size"). Namespace agent_safety: common/ is frozen. Each row owns one sealed large amount in token_amount_large: the intake
-- seals the amount in the transaction that inserts this row, as every owner of a large amount does. A window limit setting
-- refers to an amount by its id ("Limits as large amounts"); the budget store accepts only ids that have a row here, so a
-- spend's amount, which the window prune deletes, can never become a limit. No foreign keys, like the rest of the tier.
CREATE TABLE token_amount_intake (
    amount_id  uuid        PRIMARY KEY,
    created_by uuid        NOT NULL,
    created_at timestamptz NOT NULL
);

-- The runtime role records intakes and reads them when it resolves a limit. Rows are never edited.
GRANT SELECT, INSERT, DELETE ON token_amount_intake TO bazaar_app;
