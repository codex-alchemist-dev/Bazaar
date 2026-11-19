-- Large token amounts: the disk is the limit (doc 48, DECIDED 11.4). Namespace agent_safety: common/ is frozen.
-- An amount that does not fit the numeric limits (131072 digits before the point, 16383 after) is stored in chunks in this
-- same database, never in a second store or a file. Amounts are not personal data, so they are stored in clear (owner card,
-- 2026-11-19); role grants and the operator's disk encryption protect them. Plain PostgreSQL; no foreign keys: the store
-- deletes a large amount with the spend row that owns it, and a sweeper removes staged amounts that were never sealed.
--
-- token_amount_large is the header. A row is created as 'writing', its chunks are inserted in their own transactions, the
-- digit counts and the SHA-256 are recorded, and the row is sealed in the transaction that inserts the spend that refers to
-- it. The counts are the true digit counts without pad digits; the hash covers the packed chunk bytes in the order documented
-- on TokenAmountChunkCodec (fraction chunks outermost first, then integer chunks from the point outward).
CREATE TABLE token_amount_large (
    id              uuid        PRIMARY KEY DEFAULT uuidv7(),
    integer_digits  bigint      CHECK (integer_digits >= 0),
    fraction_digits bigint      CHECK (fraction_digits >= 0),
    sha256          bytea       CHECK (octet_length(sha256) = 32),
    state           text        NOT NULL DEFAULT 'writing' CHECK (state IN ('writing', 'sealed')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    -- A sealed amount has its counts and hash; only a staged one may still lack them.
    CONSTRAINT token_amount_large_sealed_complete CHECK (
        state = 'writing' OR (integer_digits IS NOT NULL AND fraction_digits IS NOT NULL AND sha256 IS NOT NULL))
);

-- The sweeper finds staged amounts by age.
CREATE INDEX token_amount_large_writing_created_at ON token_amount_large (created_at) WHERE state = 'writing';

-- The digits: two per byte (base 100), aligned at the decimal point. part 0 is the integer part, numbered from the point
-- outward; part 1 is the fraction, numbered from the point onward. Every chunk holds exactly 1 MiB except the outermost.
CREATE TABLE token_amount_large_chunk (
    amount_id uuid     NOT NULL,
    part      smallint NOT NULL CHECK (part IN (0, 1)),
    seq       bigint   NOT NULL CHECK (seq >= 0),
    digits    bytea    NOT NULL CHECK (octet_length(digits) BETWEEN 1 AND 1048576),
    PRIMARY KEY (amount_id, part, seq)
);

-- Packed digits do not compress; store them out of line without trying.
ALTER TABLE token_amount_large_chunk ALTER COLUMN digits SET STORAGE EXTERNAL;

-- A spend holds its amount inline (tokens, the 0005 numeric column) or refers to a large amount (tokens_large), never both.
-- The 0001 CHECK (tokens > 0) still applies whenever tokens is set. A large amount has at most one owning spend.
ALTER TABLE agent_token_spend ALTER COLUMN tokens DROP NOT NULL;
ALTER TABLE agent_token_spend ADD COLUMN tokens_large uuid NULL;
ALTER TABLE agent_token_spend ADD CONSTRAINT agent_token_spend_one_amount CHECK ((tokens IS NULL) <> (tokens_large IS NULL));
CREATE UNIQUE INDEX agent_token_spend_tokens_large ON agent_token_spend (tokens_large) WHERE tokens_large IS NOT NULL;

-- The runtime role stages, completes and seals headers (UPDATE), and deletes amounts with their spends; chunks are written
-- once and never edited.
GRANT SELECT, INSERT, UPDATE, DELETE ON token_amount_large TO bazaar_app;
GRANT SELECT, INSERT, DELETE ON token_amount_large_chunk TO bazaar_app;
