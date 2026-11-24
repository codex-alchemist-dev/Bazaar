-- Lucky opt-in (docs/design/26-fun-systems.md, section 6). PROPOSED: kept in Lucky's own table, not the agent table.
-- One row per agent the owner has set; an agent without a row is not opted in.
CREATE TABLE lucky_opt_in (
    agent_id    uuid        PRIMARY KEY,
    opted_in    boolean     NOT NULL DEFAULT false,
    set_by      uuid        NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);
