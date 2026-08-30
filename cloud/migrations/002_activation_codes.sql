-- Activation stops being a long key in a file and becomes a short code somebody
-- types on the till.
--
-- The code alone identifies the shop, so a till no longer has to be told which
-- shop it belongs to before it can be activated — which is what made the old
-- design need a file edit per merchant.

-- A short secret is a weaker one, and the honest way to pay for that is an
-- expiry rather than more characters nobody can type.
ALTER TABLE shops ADD COLUMN IF NOT EXISTS activation_expires_at TIMESTAMPTZ;

-- Lookup is now BY the code, so it has to be indexed — and unique, because two
-- shops sharing a code would activate whichever the planner happened to find.
-- Partial: a shop that should activate no further machines has NULL here, and
-- any number of shops may be in that state at once.
CREATE UNIQUE INDEX IF NOT EXISTS idx_shops_activation
    ON shops(activation_key_hash)
 WHERE activation_key_hash IS NOT NULL;
