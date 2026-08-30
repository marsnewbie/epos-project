// Regenerates the entitlement contract fixtures.
//
//   node fixtures/entitlement/make-fixtures.mjs
//
// Signed by the **real service signer** — `cloud/src/tokens.ts` — and verified
// by the C# tests. That import is the point: a generator with its own copy of
// the signing code would keep agreeing with itself while the service drifted
// away from both.
//
// The key here is for development and tests only. Its private half is in the
// repository, so it is deliberately absent from EntitlementKeys.Production —
// a build that trusted it would accept a token anybody could mint.

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { sign, PAYLOAD_VERSION } from "../../cloud/src/tokens.ts";

const here = dirname(fileURLToPath(import.meta.url));
const privateKey = readFileSync(join(here, "dev-private.pem"), "utf8");

const DEVICE = "fixture-device-0001";

const payload = (overrides = {}) => ({
  v: PAYLOAD_VERSION,
  shopId: "demo-shop",
  deviceId: DEVICE,
  edition: "pos",
  features: [],
  terminals: 1,
  issuedAt: "2026-08-30T10:00:00.000Z",
  expiresAt: "2026-09-29T10:00:00.000Z",
  ...overrides,
});

const cases = {
  // The ordinary answer.
  current: payload(),

  // Print-only, with a seat count and a populated allow-list — the shape a
  // restricted shop actually gets.
  "print-only": payload({ edition: "print", features: ["web-orders"], terminals: 2 }),

  // Signed properly and long past its expiry. The till must trade on this.
  expired: payload({
    issuedAt: "2026-06-01T10:00:00.000Z",
    expiresAt: "2026-07-01T10:00:00.000Z",
  }),

  // Correctly signed, but issued to a different machine.
  "other-device": payload({ deviceId: "somebody-elses-till" }),

  // A payload version this build does not know.
  "future-version": payload({ v: 99 }),

  // Carries fields the till has never heard of. Must still verify and load —
  // this is the case that lets the service add a field without breaking every
  // shop that has not updated yet.
  "unknown-fields": payload({
    loyaltyTier: "gold",
    limits: { couriers: 4 },
    features: ["drivers"],
  }),
};

const written = [];
for (const [name, body] of Object.entries(cases)) {
  writeFileSync(join(here, `${name}.token`), sign(body, privateKey), "utf8");
  written.push(name);
}

// Signed, then the payload edited afterwards — what an expiry date changed by
// hand looks like on the wire.
const [, signature] = sign(payload(), privateKey).split(".");
const edited = Buffer.from(JSON.stringify(payload({ terminals: 99 })), "utf8")
  .toString("base64")
  .replace(/=+$/, "")
  .replace(/\+/g, "-")
  .replace(/\//g, "_");
writeFileSync(join(here, "tampered.token"), `${edited}.${signature}`, "utf8");
written.push("tampered");

writeFileSync(join(here, "device-id.txt"), DEVICE, "utf8");

console.log(`wrote ${written.length} fixtures: ${written.join(", ")}`);
