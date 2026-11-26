-- Agent first-run seed seams (docs/design/45-onboarding.md section 1.15a, PROPOSED; lifecycle in doc 06).
-- Adds the hold flag and the seed marker to the shared agent table (common/0010). The agents namespace owns this change, so
-- common/ stays frozen. A held agent is skipped by the automatic Created to Onboarding step until the hold is released.
-- A marker names the seed that created the agent; at most one agent carries each marker.

ALTER TABLE agent
    ADD COLUMN first_run_hold   boolean NOT NULL DEFAULT false,
    ADD COLUMN first_run_marker text    CHECK (first_run_marker IS NULL OR length(first_run_marker) BETWEEN 1 AND 128);

CREATE UNIQUE INDEX agent_first_run_marker_key ON agent (first_run_marker) WHERE first_run_marker IS NOT NULL;
