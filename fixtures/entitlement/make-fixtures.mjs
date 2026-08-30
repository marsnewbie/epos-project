// Regenerates the entitlement contract fixtures.
//
//   node fixtures/entitlement/make-fixtures.mjs
//
// These are signed by Node and verified by C#, which is the point: the till and
// the service are different runtimes, and the one thing that must never drift is
// the bytes between them. ECDSA P-256 over SHA-256 with `dsaEncoding` set to
// "ieee-p1363" — Node's default is DER, .NET's default is P1363, and a token
// signed with the wrong one verifies nowhere.
//
// The key here is for development and tests only. Its private half is in the
// repository, so it is deliberately absent from EntitlementKeys.Production —
// a build that trusted it would accept a token anybody could mint.

import { createSign } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const privateKey = readFileSync(join(here, "dev-private.pem"), "utf8");

const DEVICE = "fixture-device-0001";

const b64url = (buf) =>
  Buffer.from(buf).toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

function sign(payload) {
  const json = Buffer.from(JSON.stringify(payload), "utf8");
  const signature = createSign("SHA256")
    .update(json)
    .sign({ key: privateKey, dsaEncoding: "ieee-p1363" });
  return `${b64url(json)}.${b64url(signature)}`;
}

function payload(overrides = {}) {
  return {
    v: 1,
    shopId: "demo-shop",
    deviceId: DEVICE,
    edition: "pos",
    features: [],
    terminals: 1,
    issuedAt: "2026-08-30T10:00:00+00:00",
    expiresAt: "2026-09-29T10:00:00+00:00",
    ...overrides,
  };
}

const cases = {
  // The ordinary answer.
  current: payload(),

  // Print-only, with a seat count and a populated allow-list — the shape a
  // restricted shop actually gets.
  "print-only": payload({
    edition: "print",
    features: ["web-orders"],
    terminals: 2,
  }),

  // Signed properly and long past its expiry. The till must trade on this.
  expired: payload({
    issuedAt: "2026-06-01T10:00:00+00:00",
    expiresAt: "2026-07-01T10:00:00+00:00",
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
  writeFileSync(join(here, `${name}.token`), sign(body), "utf8");
  written.push(name);
}

// Signed, then the payload edited afterwards — what an expiry date changed by
// hand looks like on the wire.
const [head, sig] = cases.current && sign(payload()).split(".");
const tampered = JSON.parse(Buffer.from(head.replace(/-/g, "+").replace(/_/g, "/"), "base64").toString("utf8"));
tampered.edition = "pos";
tampered.terminals = 99;
writeFileSync(join(here, "tampered.token"), `${b64url(Buffer.from(JSON.stringify(tampered), "utf8"))}.${sig}`, "utf8");
written.push("tampered");

writeFileSync(
  join(here, "device-id.txt"),
  DEVICE,
  "utf8",
);

console.log(`wrote ${written.length} fixtures: ${written.join(", ")}`);
