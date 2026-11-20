-- Council vote tables (doc 10, Data and interfaces; WP3). Owner: Council. Builds on 0001 (gc, gc_append_only).
--
-- council_vote is the only mutable table. Writers update state, version, deadline_at, extensions and vault_decision_no,
-- and only through compare-and-set on version (CouncilStore.TryTransitionAsync). Ballots and their versions are
-- insert-only. council_transition is the append-only history; each row is written in the same transaction as the
-- change it describes.
--
-- Per-person encrypted fields (doc 04): reasoning is stored as (key_id, ciphertext, nonce), the same shape as
-- gc_message_version. Encryption and decryption happen in the application through a cipher port; this file only stores
-- the bytes. The plain columns carry no personal text.
--
-- Column additions beyond doc 10's list, needed to round-trip CouncilVoteRecord: proposer_id and subject_author_id.
-- Voter and proposer ids are text, matching the Council port's agent ids (docs/design/10 and 0011).

CREATE TABLE council_vote (
    vote_id            uuid        PRIMARY KEY,
    gc_id              uuid        NOT NULL REFERENCES gc (gc_id),
    subject_ref        text        NOT NULL CHECK (btrim(subject_ref) <> ''),
    kind               text        NOT NULL CHECK (kind IN ('approve_reject', 'option_choice')),
    motion_class       text        NOT NULL CHECK (motion_class IN ('ordinary', 'bigger_change')),
    state              text        NOT NULL CHECK (state IN ('collecting', 'revealed', 'follow_up', 'decided', 'escalated', 'withdrawn')),
    -- Optimistic lock: every transition writes WHERE version = expected and sets version + 1.
    version            bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    options            jsonb       NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(options) = 'array'),
    deadline_at        timestamptz NOT NULL,
    extensions         integer     NOT NULL DEFAULT 0 CHECK (extensions >= 0),
    eligible_snapshot  jsonb       NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(eligible_snapshot) = 'array'),
    proposer_id        text        NOT NULL CHECK (btrim(proposer_id) <> ''),
    subject_author_id  text        NULL,
    reveal_seed        bigint      NOT NULL,
    vault_decision_no  integer     NULL CHECK (vault_decision_no IS NULL OR vault_decision_no >= 1),
    created_at         timestamptz NOT NULL DEFAULT now(),
    -- approve/reject takes no options; option choice takes 2 to 6 (doc 10, Vote types).
    CONSTRAINT council_vote_options_match_kind CHECK (
        (kind = 'approve_reject' AND jsonb_array_length(options) = 0)
        OR (kind = 'option_choice' AND jsonb_array_length(options) BETWEEN 2 AND 6))
);
CREATE INDEX council_vote_gc_state_idx ON council_vote (gc_id, state);

-- One row per voter and phase. A voter has at most one independent and one follow-up ballot.
CREATE TABLE council_ballot (
    vote_id          uuid        NOT NULL REFERENCES council_vote (vote_id),
    voter_id         text        NOT NULL CHECK (btrim(voter_id) <> ''),
    phase            text        NOT NULL CHECK (phase IN ('independent', 'followup')),
    choice           text        NOT NULL CHECK (btrim(choice) <> ''),
    reasoning_key_id uuid        NOT NULL,
    reasoning_ct     bytea       NOT NULL,
    reasoning_nonce  bytea       NOT NULL CHECK (octet_length(reasoning_nonce) > 0),
    revealed         boolean     NOT NULL DEFAULT false,
    tainted_at_write boolean     NOT NULL DEFAULT false,
    submitted_at     timestamptz NOT NULL,
    PRIMARY KEY (vote_id, voter_id, phase)
);

-- Every ballot write appends its first version here. Reasoning is the same per-person ciphertext shape as above.
CREATE TABLE council_ballot_ver (
    vote_id          uuid        NOT NULL,
    voter_id         text        NOT NULL,
    phase            text        NOT NULL CHECK (phase IN ('independent', 'followup')),
    seq              integer     NOT NULL CHECK (seq >= 1),
    choice           text        NOT NULL CHECK (btrim(choice) <> ''),
    reasoning_key_id uuid        NOT NULL,
    reasoning_ct     bytea       NOT NULL,
    reasoning_nonce  bytea       NOT NULL CHECK (octet_length(reasoning_nonce) > 0),
    at               timestamptz NOT NULL,
    PRIMARY KEY (vote_id, voter_id, phase, seq),
    FOREIGN KEY (vote_id, voter_id, phase) REFERENCES council_ballot (vote_id, voter_id, phase)
);

-- Append-only history of vote state. version is the vote's version after the transition, so (vote_id, version) is unique.
CREATE TABLE council_transition (
    vote_id    uuid        NOT NULL REFERENCES council_vote (vote_id),
    version    bigint      NOT NULL CHECK (version >= 1),
    from_state text        NULL CHECK (from_state IN ('collecting', 'revealed', 'follow_up', 'decided', 'escalated', 'withdrawn')),
    to_state   text        NOT NULL CHECK (to_state IN ('collecting', 'revealed', 'follow_up', 'decided', 'escalated', 'withdrawn')),
    reason     text        NOT NULL CHECK (btrim(reason) <> ''),
    at         timestamptz NOT NULL,
    PRIMARY KEY (vote_id, version),
    CHECK ((version = 1) = (from_state IS NULL))
);

CREATE TRIGGER council_ballot_append_only BEFORE UPDATE OR DELETE ON council_ballot
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER council_ballot_no_truncate BEFORE TRUNCATE ON council_ballot
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER council_ballot_ver_append_only BEFORE UPDATE OR DELETE ON council_ballot_ver
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER council_ballot_ver_no_truncate BEFORE TRUNCATE ON council_ballot_ver
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER council_transition_append_only BEFORE UPDATE OR DELETE ON council_transition
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER council_transition_no_truncate BEFORE TRUNCATE ON council_transition
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();

-- Runtime role: no DELETE anywhere, and column-level UPDATE on council_vote only (as 0012 does for tasks).
GRANT SELECT, INSERT ON council_vote, council_ballot, council_ballot_ver, council_transition TO bazaar_app;
GRANT UPDATE (state, version, deadline_at, extensions, vault_decision_no) ON council_vote TO bazaar_app;
