import { createSign } from "node:crypto";

/**
 * Signing the entitlement a till carries.
 *
 * The format is fixed by `src/RingOrder.Epos.Online/EntitlementToken.cs`, and
 * the two are held together by `fixtures/entitlement` — tokens signed here and
 * verified there. Read that file before changing anything in this one.
 *
 * `dsaEncoding: "ieee-p1363"` is not optional. Node signs ECDSA in DER by
 * default and .NET verifies P1363 by default; both are correct and they never
 * interoperate. A token signed without this line verifies nowhere, and it will
 * look perfectly fine until a till rejects it.
 */

/** Payload version. Only moves when a field changes meaning — an addition never does. */
export const PAYLOAD_VERSION = 1;

/**
 * How long a token stays usable without us.
 *
 * Mirrors `EntitlementPolicy.TokenLifetime`. It is how long the cloud may be
 * unreachable, not how often anybody renews anything: the till refreshes daily
 * and every success slides the window forward, so nothing here is ever
 * renewed by hand.
 */
export const TOKEN_LIFETIME_DAYS = 30;

export type Entitlement = {
  shopId: string;
  deviceId: string;
  edition: string;
  features: string[];
  terminals: number;
};

export type Payload = Entitlement & {
  v: number;
  issuedAt: string;
  expiresAt: string;
};

const b64url = (buf: Buffer | Uint8Array): string =>
  Buffer.from(buf).toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

/**
 * Builds the payload exactly as the till expects to read it.
 *
 * Key order is not significant to the reader — it parses JSON — but it is kept
 * stable so two tokens for the same grant are byte-identical, which makes a
 * diff of two captured tokens readable when something goes wrong.
 */
export function buildPayload(entitlement: Entitlement, now = new Date()): Payload {
  const expires = new Date(now.getTime() + TOKEN_LIFETIME_DAYS * 24 * 60 * 60 * 1000);

  return {
    v: PAYLOAD_VERSION,
    shopId: entitlement.shopId,
    deviceId: entitlement.deviceId,
    edition: entitlement.edition,
    features: entitlement.features,
    terminals: entitlement.terminals,
    issuedAt: now.toISOString(),
    expiresAt: expires.toISOString(),
  };
}

/** `base64url(payload).base64url(signature)` — the JWT shape, without the header. */
export function sign(payload: Payload, privateKeyPem: string): string {
  const json = Buffer.from(JSON.stringify(payload), "utf8");

  const signature = createSign("SHA256")
    .update(json)
    .sign({ key: privateKeyPem, dsaEncoding: "ieee-p1363" });

  return `${b64url(json)}.${b64url(signature)}`;
}

export function issue(
  entitlement: Entitlement,
  privateKeyPem: string,
  now = new Date(),
): string {
  return sign(buildPayload(entitlement, now), privateKeyPem);
}
