-- 0001 (community): platform_policy (doc 36, "Data and interfaces" and "Platform policy matrix").
--
-- One row per external platform: the verification status of its agent rule, the actions the rule allows, the
-- disclosure rule, the primary source and when a person last checked it. PlatformPolicyLoader reads this table;
-- PolicyResolver.Decide turns a row into Allow or Deny. A platform with no row is Deny (unknown platform).
--
-- Forward-only plain SQL, no extensions. The table holds public policy facts, not personal data.
-- The table is seeded from PlatformPolicySeed in code, not by this migration.

CREATE TABLE platform_policy (
    platform         text  PRIMARY KEY CHECK (length(platform) > 0),
    status           text  NOT NULL CHECK (status IN ('Verified', 'Secondary', 'Unverified', 'Banned')),
    allowed_actions  jsonb NOT NULL CHECK (jsonb_typeof(allowed_actions) = 'array'),
    disclosure_rule  text  NOT NULL,
    source_url       text  NOT NULL,
    checked_on       date  NOT NULL,
    checked_by       text  NOT NULL
);
