-- Durable state of peer approval of account creation (docs/design/15-security-taint-and-approvals.md, docs/design/59-desktop-density-and-guest-display.md
-- section 7). Component migration 0001 in the account_approval namespace. The tables hold the ids and values the in-memory state held:
-- no new free text, no personal data. Votes and decisions are append-only; the runtime role only reads and inserts.

CREATE TABLE account_approval_request (
    request_id                  text        PRIMARY KEY CHECK (length(request_id) BETWEEN 1 AND 200),
    requester_agent_id          text        NOT NULL CHECK (length(requester_agent_id) BETWEEN 1 AND 200),
    service                     text        NOT NULL CHECK (length(service) BETWEEN 1 AND 255),
    action                      text        NOT NULL,
    terms                       text        NOT NULL,
    terms_url                   text,
    terms_version_or_hash       text,
    service_on_signup_whitelist boolean     NOT NULL,
    involves_spend              boolean     NOT NULL,
    in_another_persons_name     boolean     NOT NULL,
    person_steps                integer     NOT NULL CHECK (person_steps >= 0),
    created_at                  timestamptz NOT NULL,
    -- The site vote that opened the site (SiteVoteResult): all eight columns set, or none.
    site_vote_motion_kind       text,
    site_vote_subject           text,
    site_vote_reference         text,
    site_vote_finished          boolean,
    site_vote_eligible          integer,
    site_vote_approve           integer,
    site_vote_reject            integer,
    site_vote_abstain           integer,
    CHECK (num_nulls(site_vote_motion_kind, site_vote_subject, site_vote_reference, site_vote_finished,
                     site_vote_eligible, site_vote_approve, site_vote_reject, site_vote_abstain) IN (0, 8))
);

-- One row per counted peer vote, in the order cast (vote_id). The same approver cannot vote the same kind twice on one request.
CREATE TABLE account_approval_vote (
    vote_id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id         text        NOT NULL REFERENCES account_approval_request (request_id),
    approver_agent_id  text        NOT NULL CHECK (length(approver_agent_id) BETWEEN 1 AND 200),
    kind               text        NOT NULL CHECK (kind IN ('Approve', 'Refuse')),
    model_id           text        NOT NULL,
    reasoning_hash     text        NOT NULL,
    cast_at            timestamptz NOT NULL,
    CONSTRAINT account_approval_vote_once UNIQUE (request_id, approver_agent_id, kind)
);

-- Every decision a request has had, oldest first; the latest row is the current one. A pending row records the count still needed.
-- approved_by lists the approvers whose votes made up an approval, in order. A request gets at most one final decision.
CREATE TABLE account_approval_decision (
    decision_id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id             text        NOT NULL REFERENCES account_approval_request (request_id),
    outcome                text        NOT NULL CHECK (outcome IN ('pending', 'approved_by_peers', 'needs_owner', 'denied')),
    rule                   text        NOT NULL,
    pending_remaining      integer,
    pending_quorum         integer,
    person_steps           integer,
    agent_may_accept_terms boolean,
    terms_to_accept        text,
    terms_version_or_hash  text,
    expires_at             timestamptz,
    approved_by            text[],
    decided_at             timestamptz NOT NULL DEFAULT now(),
    CHECK ((outcome = 'pending') = (pending_remaining IS NOT NULL AND pending_quorum IS NOT NULL)),
    CHECK ((outcome = 'approved_by_peers') = (person_steps IS NOT NULL AND agent_may_accept_terms IS NOT NULL
                                               AND expires_at IS NOT NULL AND approved_by IS NOT NULL))
);

CREATE INDEX account_approval_decision_latest ON account_approval_decision (request_id, decision_id DESC);
CREATE UNIQUE INDEX account_approval_decision_final_once ON account_approval_decision (request_id) WHERE outcome <> 'pending';

GRANT SELECT, INSERT ON account_approval_request, account_approval_vote, account_approval_decision TO bazaar_app;
