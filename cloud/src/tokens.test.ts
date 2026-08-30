import assert from "node:assert/strict";
import { createVerify } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, it } from "node:test";
import { buildPayload, issue, sign, PAYLOAD_VERSION, TOKEN_LIFETIME_DAYS } from "./tokens.ts";

const FIXTURES = join(import.meta.dirname, "..", "..", "fixtures", "entitlement");
const privateKeyPem = readFileSync(join(FIXTURES, "dev-private.pem"), "utf8");
const publicKeyPem = readFileSync(join(FIXTURES, "dev-public.pem"), "utf8");

const grant = {
  shopId: "demo-shop",
  deviceId: "till-0001",
  edition: "pos",
  features: ["drivers"],
  terminals: 2,
};

const parts = (token: string) => {
  const [head, signature] = token.split(".");
  assert.ok(head && signature);
  return {
    payload: Buffer.from(head.replace(/-/g, "+").replace(/_/g, "/"), "base64"),
    signature: Buffer.from(signature.replace(/-/g, "+").replace(/_/g, "/"), "base64"),
  };
};

describe("the payload", () => {
  it("carries the whole grant and a thirty-day window", () => {
    const now = new Date("2026-08-30T10:00:00.000Z");
    const payload = buildPayload(grant, now);

    assert.equal(payload.v, PAYLOAD_VERSION);
    assert.equal(payload.shopId, "demo-shop");
    assert.equal(payload.deviceId, "till-0001");
    assert.deepEqual(payload.features, ["drivers"]);
    assert.equal(payload.terminals, 2);
    assert.equal(payload.issuedAt, "2026-08-30T10:00:00.000Z");
    assert.equal(payload.expiresAt, "2026-09-29T10:00:00.000Z");
  });

  /**
   * The lifetime is duplicated in two languages — here and in
   * `EntitlementPolicy.TokenLifetime`. It is how long the cloud may be
   * unreachable, and the till refreshes daily, so the two must not drift apart
   * without somebody meaning it.
   */
  it("is thirty days, matching the till", () => {
    assert.equal(TOKEN_LIFETIME_DAYS, 30);
  });

  /**
   * The payload half is byte-identical for the same grant at the same instant,
   * which is what makes two captured tokens comparable when something is wrong.
   *
   * **The signature half is not, and cannot be.** ECDSA draws a fresh random
   * nonce for every signature, so signing the same bytes twice gives two
   * different — both valid — answers. Worth knowing before anybody concludes
   * from a diff that something changed: regenerating the fixtures always
   * rewrites every file.
   */
  it("has a payload half that is stable and a signature half that is not", () => {
    const now = new Date("2026-08-30T10:00:00.000Z");

    const first = sign(buildPayload(grant, now), privateKeyPem).split(".");
    const second = sign(buildPayload(grant, now), privateKeyPem).split(".");

    assert.equal(first[0], second[0]);
    assert.notEqual(first[1], second[1]);
  });
});

describe("the signature", () => {
  it("verifies with the public half", () => {
    const { payload, signature } = parts(issue(grant, privateKeyPem));

    const ok = createVerify("SHA256")
      .update(payload)
      .verify({ key: publicKeyPem, dsaEncoding: "ieee-p1363" }, signature);

    assert.ok(ok);
  });

  /**
   * P-256 in IEEE P1363 is r and s concatenated: two 32-byte halves, always.
   * DER is variable-length and starts 0x30 — pinned here because .NET verifies
   * P1363 by default, so signing DER produces a token that verifies nowhere and
   * looks entirely normal until a till rejects it.
   */
  it("is raw r||s, not DER", () => {
    const { signature } = parts(issue(grant, privateKeyPem));

    assert.equal(signature.length, 64);
    assert.notEqual(signature[0], 0x30);
  });

  it("does not survive an edited payload", () => {
    const token = issue(grant, privateKeyPem);
    const { signature } = parts(token);

    const tampered = Buffer.from(JSON.stringify({ ...buildPayload(grant), terminals: 99 }), "utf8");

    const ok = createVerify("SHA256")
      .update(tampered)
      .verify({ key: publicKeyPem, dsaEncoding: "ieee-p1363" }, signature);

    assert.ok(!ok);
  });
});

describe("the encoding", () => {
  it("is base64url — no padding, and safe in a URL or a header", () => {
    const token = issue(grant, privateKeyPem);

    assert.ok(!token.includes("="));
    assert.ok(!token.includes("+"));
    assert.ok(!token.includes("/"));
    assert.equal(token.split(".").length, 2);
  });
});
