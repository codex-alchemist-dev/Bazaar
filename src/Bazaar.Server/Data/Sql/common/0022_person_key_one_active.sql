-- At most one live person key per person (docs/design/04 Question 2). PersonKeyStore already serialises first-key creation with
-- an advisory lock; this index keeps the invariant even if a writer bypasses that lock. Erased rows (destroyed_at set) stay and
-- do not count, because erasure never deletes a key row.
CREATE UNIQUE INDEX person_key_one_active_idx ON person_key (person_id) WHERE destroyed_at IS NULL;
