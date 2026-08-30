import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createVerify } from "node:crypto";
import { join } from "node:path";
import { describe, it } from "node:test";
import { activate, sync, type Options } from "./routes.ts";
import { hashSecret, MemoryStore, type Shop } from "./store.ts";

const FIXTURES = join(import.meta.dirname, "..", "..", "fixtures", "entitlement");
const privateKeyPem = readFileSync(join(FIXTURES, "dev-private.pem"), "utf8");
const publicKeyPem = readFileSync(join(FIXTURES, "dev-public.pem"), "utf8");

const DEVICE = "till-0001";
const CLIENT = "1.4.2";

function setup(shop: Partial<Shop> = {}, activationKey: string | null = "let-me-in") {
  const store = new MemoryStore();
  store.shops.set("demo-shop", {
    id: "demo-shop",
    edition: "pos",
    features: [],
    terminals: 1,
    activationKeyHash: activationKey === null ? null : hashSecret(activationKey),
    ...shop,
  });

  const options: Options = { store, privateKeyPem, minClientVersion: null };
  return { store, options };
}

/** Reads a token back the way the till does, so assertions are about what a shop actually receives. */
function read(token: string): Record<string, unknown> {
  const [head, signature] = token.split(".");
  assert.ok(head && signature, "a token is two parts");

  const payload = Buffer.from(head.replace(/-/g, "+").replace(/_/g, "/"), "base64");

  const verified = createVerify("SHA256")
    .update(payload)
    .verify(
      { key: publicKeyPem, dsaEncoding: "ieee-p1363" },
      Buffer.from(signature.replace(/-/g, "+").replace(/_/g, "/"), "base64"),
    );
  assert.ok(verified, "the signature must verify against the public half");

  return JSON.parse(payload.toString("utf8")) as Record<string, unknown>;
}

describe("activate", () => {
  it("exchanges the activation key for a secret and a first token", async () => {
    const { store, options } = setup();

    const reply = await activate(
      { shopId: "demo-shop", deviceId: DEVICE, activationKey: "let-me-in", clientVersion: CLIENT },
      options,
    );

    assert.equal(reply.status, 200);
    assert.equal(typeof reply.body.deviceSecret, "string");

    const payload = read(reply.body.token as string);
    assert.equal(payload.shopId, "demo-shop");
    assert.equal(payload.deviceId, DEVICE);

    // Stored only as a hash — the plain secret is shown once and never again.
    const device = await store.device(DEVICE);
    assert.ok(device);
    assert.notEqual(device.secretHash, reply.body.deviceSecret);
  });

  it("binds the token to the machine that asked", async () => {
    const { options } = setup();

    const first = await activate({ shopId: "demo-shop", deviceId: "till-a", activationKey: "let-me-in" }, options);
    const second = await activate({ shopId: "demo-shop", deviceId: "till-b", activationKey: "let-me-in" }, options);

    assert.equal(read(first.body.token as string).deviceId, "till-a");
    assert.equal(read(second.body.token as string).deviceId, "till-b");
  });

  it("refuses a wrong key without saying which part was wrong", async () => {
    const { options } = setup();

    for (const attempt of [
      { shopId: "demo-shop", deviceId: DEVICE, activationKey: "wrong" },
      { shopId: "no-such-shop", deviceId: DEVICE, activationKey: "let-me-in" },
    ]) {
      const reply = await activate(attempt, options);
      assert.equal(reply.status, 401);
      assert.equal(reply.body.error, "unknown shop or activation key");
    }
  });

  it("refuses a shop that should activate no further machines", async () => {
    const { options } = setup({}, null);

    const reply = await activate({ shopId: "demo-shop", deviceId: DEVICE, activationKey: "let-me-in" }, options);
    assert.equal(reply.status, 401);
  });

  /**
   * The recovery path. A till whose connection dropped between our answer and
   * its write holds an activation key and no secret; refusing a second
   * activation would strand that machine for good.
   */
  it("activating twice issues a fresh secret rather than refusing", async () => {
    const { options } = setup();
    const body = { shopId: "demo-shop", deviceId: DEVICE, activationKey: "let-me-in" };

    const first = await activate(body, options);
    const second = await activate(body, options);

    assert.equal(second.status, 200);
    assert.notEqual(first.body.deviceSecret, second.body.deviceSecret);

    // And the newest one is the one that works.
    const check = await sync(
      { deviceId: DEVICE, deviceSecret: second.body.deviceSecret, clientVersion: CLIENT },
      options,
    );
    assert.equal(check.status, 200);
  });

  it("wants all three of shop, device and key", async () => {
    const { options } = setup();

    for (const partial of [
      { deviceId: DEVICE, activationKey: "let-me-in" },
      { shopId: "demo-shop", activationKey: "let-me-in" },
      { shopId: "demo-shop", deviceId: DEVICE },
      { shopId: "demo-shop", deviceId: "   ", activationKey: "let-me-in" },
    ]) {
      assert.equal((await activate(partial, options)).status, 400);
    }
  });
});

describe("sync", () => {
  async function activated(shop: Partial<Shop> = {}) {
    const { store, options } = setup(shop);
    const reply = await activate({ shopId: "demo-shop", deviceId: DEVICE, activationKey: "let-me-in" }, options);
    return { store, options, secret: reply.body.deviceSecret as string };
  }

  it("answers a known device with the shop's current grant", async () => {
    const { options, secret } = await activated({ edition: "print", features: ["web-orders"], terminals: 2 });

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, clientVersion: CLIENT }, options);

    assert.equal(reply.status, 200);
    const payload = read(reply.body.token as string);
    assert.equal(payload.edition, "print");
    assert.deepEqual(payload.features, ["web-orders"]);
    assert.equal(payload.terminals, 2);
  });

  /**
   * A shop that stops paying is answered, not refused. Changing its row gives us
   * exactly what it keeps; refusing would surrender that control and land the
   * change thirty days later on a day nobody chose.
   */
  it("a downgraded shop is told what it now has, not turned away", async () => {
    const { store, options, secret } = await activated({ edition: "pos", features: ["drivers"] });

    const shop = store.shops.get("demo-shop");
    assert.ok(shop);
    shop.edition = "print";
    shop.features = [];

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret }, options);

    assert.equal(reply.status, 200);
    assert.equal(read(reply.body.token as string).edition, "print");
  });

  it("refuses an unknown device or a wrong secret", async () => {
    const { options, secret } = await activated();

    assert.equal((await sync({ deviceId: "never-seen", deviceSecret: secret }, options)).status, 401);
    assert.equal((await sync({ deviceId: DEVICE, deviceSecret: "guessed" }, options)).status, 401);
  });

  it("refuses a device whose shop has been deleted", async () => {
    const { store, options, secret } = await activated();
    store.shops.delete("demo-shop");

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret }, options);
    assert.equal(reply.status, 401);
  });

  it("records what build asked, which is what makes retiring a version safe", async () => {
    const { store, options, secret } = await activated();

    await sync({ deviceId: DEVICE, deviceSecret: secret, clientVersion: "1.4.2" }, options);

    assert.equal(store.seen.get(DEVICE)?.clientVersion, "1.4.2");
  });

  it("tells an old build to update instead of refusing it", async () => {
    const { options, secret } = await activated();
    const gated: Options = { ...options, minClientVersion: "2.0.0" };

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, clientVersion: "1.4.2" }, gated);

    // 426, not 401: the till keeps trading on what it has and updates itself.
    assert.equal(reply.status, 426);
  });

  it("lets a till through when it cannot say what version it is", async () => {
    const { options, secret } = await activated();
    const gated: Options = { ...options, minClientVersion: "2.0.0" };

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret }, gated);

    assert.equal(reply.status, 200);
  });
});
