-- Operator settings values (doc 03 "Settings registry", doc 04 "Settings"). The code registry owns keys, types, defaults and floors;
-- this table holds only the values. Every write is audited through the settings audit sink (doc 15).
CREATE TABLE setting (
    key        text        NOT NULL,
    scope      text        NOT NULL CHECK (scope IN ('install', 'project', 'agent')),
    scope_id   text        NOT NULL DEFAULT '',
    value      jsonb       NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text        NOT NULL,
    PRIMARY KEY (key, scope, scope_id),
    CHECK ((scope = 'install') = (scope_id = ''))
);
