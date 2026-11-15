-- In-flight tool calls (doc 06 crash recovery, ToolDispatcher). One row per call key, "{turnId:D}:{callId}".
-- A row in state 'begun' means the call may have run. It stays unfinished until CompleteAsync, whatever lease_expires_at says:
-- the lease reclaims the row only, it never allows the call to run again. Namespace turns_inflight: the turns namespace is
-- taken by the taint store, and common/ is frozen.
CREATE TABLE in_flight_call (
    call_key         text        PRIMARY KEY,
    state            text        NOT NULL CHECK (state IN ('begun', 'completed')),
    begun_at         timestamptz NOT NULL DEFAULT now(),
    lease_expires_at timestamptz NOT NULL,
    completed_at     timestamptz NULL,
    CONSTRAINT in_flight_call_completed_has_time CHECK (state <> 'completed' OR completed_at IS NOT NULL)
);

REVOKE ALL ON in_flight_call FROM PUBLIC;
REVOKE ALL ON in_flight_call FROM bazaar_app;
GRANT INSERT, SELECT, UPDATE ON in_flight_call TO bazaar_app;
