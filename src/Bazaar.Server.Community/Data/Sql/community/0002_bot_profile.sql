-- 0002: bot_profile table (doc 36, "Bot profile conventions" and "Data and interfaces"). Owner: WP2.
--
-- One disclosed identity per agent and platform. The profile is shown on every outbound body (footer) and in the
-- platform profile. Columns follow the BotProfile record; sponsor_id holds the sponsor's name reference.
--   contact      is an encryptable per-person field (doc 04). TODO(encryption): encryption is not yet wired,
--                so the value is stored as plain text until the per-person field encryption lands.
--   disclosure_url must be https; the application validates this (BotProfileBuilder.Validate), not the schema.
CREATE TABLE bot_profile (
    agent_id        text NOT NULL,
    platform        text NOT NULL,
    display_name    text NOT NULL,
    sponsor_id      text NOT NULL,
    contact         text NOT NULL, -- TODO(encryption): per-person encrypted field (doc 04), not yet wired
    disclosure_url  text NOT NULL,
    PRIMARY KEY (agent_id, platform)
);
