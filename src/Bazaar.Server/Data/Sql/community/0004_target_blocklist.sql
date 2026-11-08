-- 0004: target_blocklist (doc 36, "Rate limits, audit and takedown", Kill switch). Owner: WP5.
--
-- One row per blocklisted target on a platform. A target enters the list when a person objects to it or a moderator
-- bans it, and it is never removed: the owner decides any reopening (doc 36). TargetBlocklist (Blocklist/) is the only
-- writer. Re-adding an existing target does nothing (ON CONFLICT DO NOTHING), so the first reason and adder are kept.
--
-- The table is append-only. The runtime role (bazaar_app) gets INSERT and SELECT only; no statement updates or
-- deletes a row.
--
-- Columns:
--   target_ref  the platform's reference for the target (for example a subreddit, repo or thread id), as text.
--   reason      short human-readable reason. Not personal data.
--   added_by    the person or agent that added the entry.

CREATE TABLE target_blocklist (
    platform   text        NOT NULL,
    target_ref text        NOT NULL,
    reason     text        NOT NULL,
    added_by   text        NOT NULL,
    added_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (platform, target_ref)
);

REVOKE ALL ON target_blocklist FROM PUBLIC;
GRANT INSERT, SELECT ON target_blocklist TO bazaar_app;
