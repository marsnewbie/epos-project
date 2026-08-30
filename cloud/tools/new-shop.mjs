// Prepares a shop for the entitlement service.
//
//   node cloud/tools/new-shop.mjs <shop-id> [pos|print] [terminals]
//
// Prints two things: the SQL to run against the service's database, and the
// block to paste into that shop's secrets.json.
//
// It exists because the activation key is **stored hashed and delivered
// plain**, and doing that by hand is how a shop ends up with a key that cannot
// activate and an error that says only "unknown shop or activation key".

import { createHash, randomBytes } from "node:crypto";

const [shopId, edition = "pos", terminals = "1"] = process.argv.slice(2);

if (!shopId) {
  console.error("usage: node cloud/tools/new-shop.mjs <shop-id> [pos|print] [terminals]");
  console.error("       the shop id is the bundle's shop.slug — they must match");
  process.exit(1);
}

if (edition !== "pos" && edition !== "print") {
  console.error(`edition must be "pos" or "print", not "${edition}"`);
  process.exit(1);
}

// Long enough that it never needs a lockout, short enough to read down a phone.
const activationKey = randomBytes(24).toString("base64url");
const hash = createHash("sha256").update(activationKey, "utf8").digest("hex");

console.log(`
-- ── Run this against the service's Postgres ──────────────────────────────

INSERT INTO shops (id, edition, features, terminals, activation_key_hash, note)
VALUES ('${shopId}', '${edition}', '{}', ${Number(terminals)}, '${hash}',
        'created ${new Date().toISOString().slice(0, 10)}')
ON CONFLICT (id) DO UPDATE
   SET edition = excluded.edition,
       terminals = excluded.terminals,
       activation_key_hash = excluded.activation_key_hash;

-- An empty features list restricts nothing — only a populated one is an
-- allow-list. Leave it empty unless you are deliberately gating a module.


-- ── Paste into ringorder-epos-shops/${shopId}/secrets.json ───────────────

  "cloud": {
    "baseUrl": "https://<your-service>.up.railway.app",
    "activationKey": "${activationKey}"
  }

-- The key is shown once. It is stored only as a hash, so it cannot be read
-- back out of the database — generate a new one if it is lost, which costs
-- nothing but a re-import.
`);
