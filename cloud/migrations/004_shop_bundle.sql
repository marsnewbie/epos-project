-- The shop bundle, so a new till stops needing a file copied onto it by hand.
--
-- It is the same JSON that used to be dropped into the till's profile folder.
-- Holding it here means a menu change is uploaded once and every till belonging
-- to that shop picks it up, instead of somebody visiting with a memory stick.

ALTER TABLE shops ADD COLUMN IF NOT EXISTS bundle TEXT;

-- SHA-256 of the bundle. The till compares this with what it last applied, so a
-- shop that has not changed downloads nothing at all — the version comes down on
-- every sync, the bundle only when it differs.
ALTER TABLE shops ADD COLUMN IF NOT EXISTS bundle_version TEXT;

ALTER TABLE shops ADD COLUMN IF NOT EXISTS bundle_updated_at TIMESTAMPTZ;

-- Deliberately no `secrets` column, and the omission is the decision.
--
-- A bundle is a menu, printers, delivery zones and staff names — the things that
-- make a till this shop's till. Credentials are not in it: the website password
-- and the postcode-lookup key are typed once in Settings, and putting them here
-- would mean this service held merchants' passwords to somebody else's systems
-- in exchange for saving four lines of typing on one day of a shop's life.
