-- Mirror Agent storage (doc 26 section 5, Data and interfaces). Owner: Mirror. No foreign keys: rows are owner-scope and opaque.
--
-- Encrypted payloads (C01): the signal payload and the whole profile body (rules and evidence) are stored as
-- (key_id, nonce, ciphertext), sealed in the application through IMirrorCipher. The plain columns carry no free text from the
-- owner. Wipe deletes every row here; erasure destroys the key and the sealed columns become unreadable.

CREATE TABLE mirror_signal (
    id          uuid             PRIMARY KEY,
    kind        text             NOT NULL CHECK (kind IN (
                    'CardAccepted', 'CardRejected', 'OwnerEdit', 'ReviewComment', 'CouncilTieBreak', 'PreferenceStatement')),
    topic       text             NOT NULL CHECK (btrim(topic) <> ''),
    direction   text             NOT NULL CHECK (direction IN ('Prefer', 'Avoid')),
    source_ref  text             NOT NULL CHECK (btrim(source_ref) <> ''),
    weight      double precision NOT NULL,
    key_id      uuid             NOT NULL,
    nonce       bytea            NOT NULL CHECK (octet_length(nonce) = 12),
    ciphertext  bytea            NOT NULL,
    created_at  timestamptz      NOT NULL
);

CREATE INDEX mirror_signal_created_idx ON mirror_signal (created_at, id);

-- Versions only grow. A version holds the sealed rules and their evidence refs.
CREATE TABLE mirror_profile (
    version     integer     PRIMARY KEY CHECK (version >= 1),
    key_id      uuid        NOT NULL,
    nonce       bytea       NOT NULL CHECK (octet_length(nonce) = 12),
    ciphertext  bytea       NOT NULL,
    created_at  timestamptz NOT NULL
);

-- One row: whether learning is paused.
CREATE TABLE mirror_state (
    id      smallint PRIMARY KEY CHECK (id = 1),
    paused  boolean  NOT NULL DEFAULT false
);

-- Rules the owner deleted, as "Direction:topic". They stay out of every rebuild.
CREATE TABLE mirror_suppressed (
    rule_key text PRIMARY KEY CHECK (btrim(rule_key) <> '')
);
