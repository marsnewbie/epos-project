-- The entitlement authority's whole schema.
--
-- Two tables, and what is *absent* from them is the design: no orders, no
-- customers, no money. The till is the system of record and this service says
-- only what a shop has bought. Adding a column that holds trading data is a
-- decision that belongs in docs/CLOUD.md with a reason.

CREATE TABLE IF NOT EXISTS shops (
  id                   TEXT PRIMARY KEY,           -- the shop bundle's slug
  edition              TEXT        NOT NULL DEFAULT 'pos',
  features             TEXT[]      NOT NULL DEFAULT '{}',
  terminals            INTEGER     NOT NULL DEFAULT 1,

  -- Hashed, never stored in the clear. Null once a shop should activate no
  -- further machines; existing devices carry on with their own secrets.
  activation_key_hash  TEXT,

  note                 TEXT,                       -- ours, for support
  created_at           TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- An empty features array restricts nothing — only a populated one is an
-- allow-list. This mirrors EntitlementState.Allows on the till, and it is the
-- reason turning entitlements on changes nothing for any existing shop.
COMMENT ON COLUMN shops.features IS
  'Empty restricts nothing; only a populated list is an allow-list. See docs/CLOUD.md.';

CREATE TABLE IF NOT EXISTS devices (
  id              TEXT PRIMARY KEY,                -- the till's own random identifier
  shop_id         TEXT        NOT NULL REFERENCES shops(id) ON DELETE CASCADE,
  secret_hash     TEXT        NOT NULL,

  -- What the till last said it was. This is what eventually makes it safe to
  -- retire a protocol version: without it, "has everyone updated?" is a guess.
  client_version  TEXT,
  last_seen       TIMESTAMPTZ,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_devices_shop ON devices(shop_id);

-- "Who is still on an old build, and who has gone quiet" — the two questions
-- this service exists to be able to answer about the estate.
CREATE INDEX IF NOT EXISTS idx_devices_seen ON devices(last_seen);
