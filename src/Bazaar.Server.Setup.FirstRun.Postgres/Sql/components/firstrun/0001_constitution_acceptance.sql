-- First-run Constitution acceptance (docs/design/45-onboarding.md section 1.15, doc 24: no activation without an accepted commit).
-- One row per owner act of acceptance for a Constitution commit. Append-only in practice: a repeat acceptance of the same commit
-- by the same owner is a no-op through the primary key.

CREATE TABLE firstrun_constitution_acceptance (
    owner_id    uuid        NOT NULL,
    commit_sha  text        NOT NULL CHECK (commit_sha ~ '^[0-9a-f]{40}$'),
    accepted_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, commit_sha)
);
