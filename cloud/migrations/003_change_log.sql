-- The tills' change logs, as received.
--
-- This is the half of the tamper-evidence the chain cannot provide on its own:
-- deleting the newest entry on a till leaves nothing behind to disagree with it,
-- but a till whose next batch does not continue from what we already hold is
-- telling us something was removed.

CREATE TABLE IF NOT EXISTS change_log (
  -- The till's own UUID. Primary key, so a batch re-sent because the answer was
  -- lost on the way back inserts nothing the second time.
  id          TEXT PRIMARY KEY,

  device_id   TEXT   NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
  shop_id     TEXT   NOT NULL REFERENCES shops(id)   ON DELETE CASCADE,

  -- Local to the till that wrote it, and only ordered within one device.
  seq         BIGINT NOT NULL,

  terminal_id TEXT   NOT NULL,
  entity      TEXT   NOT NULL,
  entity_id   TEXT   NOT NULL,
  op          TEXT   NOT NULL,

  -- TEXT, deliberately, and never JSONB. JSONB reorders keys and drops
  -- whitespace, which changes the bytes — and the bytes are what was hashed.
  -- Stored verbatim, an entry can still be re-verified years later; stored as
  -- JSONB it could not be, and nothing would say so.
  payload     TEXT   NOT NULL,

  -- Also verbatim, for the same reason: a timestamp round-tripped through
  -- TIMESTAMPTZ comes back spelled differently and stops hashing to itself.
  at          TEXT   NOT NULL,

  -- Derived from `at` for querying. Never hashed, so its formatting is free to
  -- be whatever Postgres likes.
  at_utc      TIMESTAMPTZ NOT NULL,

  staff_id    TEXT,
  prev_hash   TEXT   NOT NULL,
  hash        TEXT   NOT NULL,
  received_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_change_log_device_seq ON change_log(device_id, seq);
CREATE INDEX IF NOT EXISTS idx_change_log_shop ON change_log(shop_id, at_utc);
CREATE INDEX IF NOT EXISTS idx_change_log_entity ON change_log(shop_id, entity, entity_id);

-- Where each till's chain had got to when we last heard from it, so the next
-- batch can be required to continue from it.
ALTER TABLE devices ADD COLUMN IF NOT EXISTS chain_head TEXT;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS chain_seq  BIGINT NOT NULL DEFAULT 0;

-- Set when a batch did not add up, and never cleared automatically. A chain
-- that broke once is a thing a person looks at; clearing it on the next good
-- batch would hide exactly the event this table exists to catch.
ALTER TABLE devices ADD COLUMN IF NOT EXISTS chain_broken_at     TIMESTAMPTZ;
ALTER TABLE devices ADD COLUMN IF NOT EXISTS chain_broken_reason TEXT;
