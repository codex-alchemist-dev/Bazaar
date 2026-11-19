-- Council high-impact flag (doc 10, Follow-up on dissent [PROPOSED]; doc 04). Owner: Council. Builds on council/0001.
--
-- The proposer sets high_impact when the vote opens; it is written once at insert and never updated. Rows that exist
-- before this migration read as false, so those votes tally exactly as before.

ALTER TABLE council_vote ADD COLUMN high_impact boolean NOT NULL DEFAULT false;
