-- Council membership handoff (doc 10, Membership and role votes [PROPOSED]). Owner: Council. Builds on council/0002.
--
-- membership_kind and membership_subject_id are set when the vote opens and never change; both are set (a membership
-- vote) or both null (any other vote). membership_outcome_delivered_at is null until every registered membership sink
-- accepted the outcome; the deadline sweeper re-drives decided membership votes where it is still null. Rows that exist
-- before this migration are not membership votes.

ALTER TABLE council_vote ADD COLUMN membership_kind text NULL;
ALTER TABLE council_vote ADD COLUMN membership_subject_id text NULL;
ALTER TABLE council_vote ADD COLUMN membership_outcome_delivered_at timestamptz NULL;

ALTER TABLE council_vote ADD CONSTRAINT council_vote_membership_kind_check
    CHECK (membership_kind IS NULL OR membership_kind IN ('hire', 'promote', 'retire', 'role_change', 'constitution_change'));
ALTER TABLE council_vote ADD CONSTRAINT council_vote_membership_pair_check
    CHECK ((membership_kind IS NULL) = (membership_subject_id IS NULL)
        AND (membership_subject_id IS NULL OR btrim(membership_subject_id) <> ''));

CREATE INDEX council_vote_membership_undelivered_idx ON council_vote (vote_id)
    WHERE membership_kind IS NOT NULL AND membership_outcome_delivered_at IS NULL;
