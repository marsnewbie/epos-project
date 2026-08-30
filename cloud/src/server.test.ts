import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { after, before, describe, it } from "node:test";
import { createApp, MAX_BODY_BYTES } from "./server.ts";
import { hashSecret, MemoryStore } from "./store.ts";

/**
 * The transport, over a real socket.
 *
 * The handlers are tested as functions in `routes.test.ts`; what is left here is
 * everything a till could do to the service that is not a well-formed request —
 * and the headers a proxy would otherwise get wrong.
 */

const privateKeyPem = readFileSync(
  join(import.meta.dirname, "..", "..", "fixtures", "entitlement", "dev-private.pem"),
  "utf8",
);

const store = new MemoryStore();
store.shops.set("demo-shop", {
  id: "demo-shop",
  edition: "pos",
  features: [],
  terminals: 1,
  activationKeyHash: hashSecret("K7M2P9QR"),
  activationExpiresAt: null,
});

let healthy = true;
const server = createApp({
  store,
  privateKeyPem,
  minClientVersion: null,
  ping: async () => {
    if (!healthy) throw new Error("down");
  },
});

let base = "";

before(async () => {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

after(() => new Promise<void>((resolve) => server.close(() => resolve())));

const post = (path: string, body: unknown, raw?: string) =>
  fetch(`${base}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: raw ?? JSON.stringify(body),
  });

describe("health", () => {
  it("answers, and says so when the database does not", async () => {
    const ok = await fetch(`${base}/healthz`);
    assert.equal(ok.status, 200);
    assert.deepEqual(await ok.json(), { ok: true });

    healthy = false;
    const bad = await fetch(`${base}/healthz`);
    assert.equal(bad.status, 503);
    healthy = true;
  });
});

describe("the transport", () => {
  it("takes an activation over a real socket", async () => {
    const response = await post("/v1/activate", {
      deviceId: "till-http",
      activationCode: "K7M2-P9QR",
      clientVersion: "1.4.2",
    });

    assert.equal(response.status, 200);
    const body = (await response.json()) as Record<string, unknown>;
    assert.equal(typeof body.token, "string");
    assert.equal(typeof body.deviceSecret, "string");
  });

  /**
   * An entitlement is per-device. One sitting in a proxy would hand a shop
   * somebody else's plan, which is the sort of fault that is invisible until it
   * is a very odd support call.
   */
  it("forbids caching, always", async () => {
    for (const response of [
      await fetch(`${base}/healthz`),
      await post("/v1/sync", { deviceId: "nobody", deviceSecret: "nothing" }),
    ]) {
      assert.equal(response.headers.get("cache-control"), "no-store");
    }
  });

  it("refuses anything that is not a small JSON object", async () => {
    for (const raw of ["not json", "[1,2,3]", '"a string"', "null", ""]) {
      const response = await post("/v1/sync", null, raw);
      assert.equal(response.status, 400, `expected 400 for ${raw || "(empty)"}`);
    }
  });

  it("refuses a body larger than a till would ever send", async () => {
    const huge = JSON.stringify({ deviceId: "x".repeat(MAX_BODY_BYTES + 100) });

    assert.equal((await post("/v1/sync", null, huge)).status, 400);
  });

  it("has nothing to say to other methods and other paths", async () => {
    assert.equal((await fetch(`${base}/v1/sync`)).status, 405);
    assert.equal((await post("/v1/whatever", {})).status, 404);
    assert.equal((await post("/", {})).status, 404);
  });

  /** A wrong secret must not come back with a stack trace or a hint. */
  it("says only that a device is unknown", async () => {
    const response = await post("/v1/sync", { deviceId: "ghost", deviceSecret: "guess" });

    assert.equal(response.status, 401);
    assert.deepEqual(await response.json(), { error: "unknown device" });
  });
});
