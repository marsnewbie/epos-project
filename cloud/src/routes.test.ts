import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createVerify } from "node:crypto";
import { join } from "node:path";
import { describe, it } from "node:test";
import { activate, adminListShops, adminSaveShop, sync, type Options } from "./routes.ts";
import { hashSecret, MemoryStore, type Shop } from "./store.ts";
import { GENESIS, hashOf, type ChangeEntry } from "./chain.ts";

const FIXTURES = join(import.meta.dirname, "..", "..", "fixtures", "entitlement");
const privateKeyPem = readFileSync(join(FIXTURES, "dev-private.pem"), "utf8");
const publicKeyPem = readFileSync(join(FIXTURES, "dev-public.pem"), "utf8");

const DEVICE = "till-0001";
const CLIENT = "1.4.2";
const CODE = "K7M2P9QR";

function setup(shop: Partial<Shop> = {}, code: string | null = CODE, expiresAt?: Date) {
  const store = new MemoryStore();
  store.shops.set("demo-shop", {
    id: "demo-shop",
    edition: "pos",
    features: [],
    terminals: 1,
    activationKeyHash: code === null ? null : hashSecret(code),
    activationExpiresAt: expiresAt ?? null,
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
  it("turns a typed code into a secret, a token and a shop name", async () => {
    const { store, options } = setup();

    const reply = await activate(
      { deviceId: DEVICE, activationCode: CODE, clientVersion: CLIENT },
      options,
    );

    assert.equal(reply.status, 200);
    assert.equal(typeof reply.body.deviceSecret, "string");

    // Echoed so the till can say which shop it joined rather than just "done".
    assert.equal(reply.body.shopId, "demo-shop");

    const payload = read(reply.body.token as string);
    assert.equal(payload.shopId, "demo-shop");
    assert.equal(payload.deviceId, DEVICE);

    const device = await store.device(DEVICE);
    assert.ok(device);
    assert.notEqual(device.secretHash, reply.body.deviceSecret);
  });

  /**
   * The whole point of the redesign. Nobody tells the till which shop it is —
   * the code says so, which is what removes the file somebody had to edit per
   * merchant.
   */
  it("needs no shop id, because the code is the shop", async () => {
    const { options } = setup();

    const reply = await activate({ deviceId: DEVICE, activationCode: CODE }, options);

    assert.equal(reply.status, 200);
    assert.equal(read(reply.body.token as string).shopId, "demo-shop");
  });

  it("accepts a code the way a person actually types it", async () => {
    for (const typed of ["K7M2P9QR", "k7m2p9qr", "K7M2-P9QR", " k7m2 p9qr ", "K7M2-P9QR\n"]) {
      const { options } = setup();
      const reply = await activate({ deviceId: DEVICE, activationCode: typed }, options);

      assert.equal(reply.status, 200, `expected ${typed} to be accepted`);
    }
  });

  /**
   * Crockford's substitutions. Over a telephone `I` is a one and `O` is a zero,
   * and a code refused for that would send someone hunting for a fault that is
   * not there.
   */
  it("forgives the letters people hear as digits", async () => {
    const { store, options } = setup();
    store.shops.set("demo-shop", {
      ...store.shops.get("demo-shop")!,
      activationKeyHash: hashSecret("10ZER011"),
    });

    const reply = await activate({ deviceId: DEVICE, activationCode: "IOZERO1L" }, options);

    assert.equal(reply.status, 200);
  });

  it("binds the token to the machine that asked", async () => {
    const { options } = setup();

    const a = await activate({ deviceId: "till-a", activationCode: CODE }, options);
    const b = await activate({ deviceId: "till-b", activationCode: CODE }, options);

    assert.equal(read(a.body.token as string).deviceId, "till-a");
    assert.equal(read(b.body.token as string).deviceId, "till-b");
  });

  /**
   * A wrong code and an expired one are the same answer. There is nothing useful
   * to learn from the difference, and telling them apart is how a guess becomes
   * an oracle.
   */
  it("says only that a code is not recognised", async () => {
    const wrong = setup();
    const expired = setup({}, CODE, new Date("2020-01-01T00:00:00Z"));
    const withdrawn = setup({}, null);

    for (const [name, { options }] of [
      ["a wrong code", wrong],
      ["an expired code", expired],
      ["a shop that should activate nothing further", withdrawn],
    ] as const) {
      const code = name === "a wrong code" ? "ZZZZZZZZ" : CODE;
      const reply = await activate({ deviceId: DEVICE, activationCode: code }, options);

      assert.equal(reply.status, 401, name);
      assert.equal(reply.body.error, "that code is not recognised", name);
    }
  });

  it("refuses anything that is not a code before it looks anything up", async () => {
    const { store, options } = setup();
    let looked = false;
    store.shopForActivation = async () => {
      looked = true;
      return null;
    };

    for (const bad of ["", "short", "TOOMANYCHARS", "!!!!!!!!", undefined]) {
      const reply = await activate({ deviceId: DEVICE, activationCode: bad }, options);
      assert.equal(reply.status, 400, `expected 400 for ${String(bad)}`);
    }

    assert.equal(looked, false);
  });

  /**
   * The recovery path. A till whose connection dropped between our answer and
   * its write holds a code and no secret; refusing a second activation would
   * strand that machine for good.
   */
  it("activating twice issues a fresh secret rather than refusing", async () => {
    const { options } = setup();
    const body = { deviceId: DEVICE, activationCode: CODE };

    const first = await activate(body, options);
    const second = await activate(body, options);

    assert.equal(second.status, 200);
    assert.notEqual(first.body.deviceSecret, second.body.deviceSecret);

    const check = await sync(
      { deviceId: DEVICE, deviceSecret: second.body.deviceSecret, clientVersion: CLIENT },
      options,
    );
    assert.equal(check.status, 200);
  });
});

describe("sync", () => {
  async function activated(shop: Partial<Shop> = {}) {
    const { store, options } = setup(shop);
    const reply = await activate({ deviceId: DEVICE, activationCode: CODE }, options);
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

    assert.equal((await sync({ deviceId: DEVICE, deviceSecret: secret }, options)).status, 401);
  });

  it("records what build asked, which is what makes retiring a version safe", async () => {
    const { store, options, secret } = await activated();

    await sync({ deviceId: DEVICE, deviceSecret: secret, clientVersion: "1.4.2" }, options);

    assert.equal(store.seen.get(DEVICE)?.clientVersion, "1.4.2");
  });

  it("tells an old build to update instead of refusing it", async () => {
    const { options, secret } = await activated();
    const gated: Options = { ...options, minClientVersion: "2.0.0" };

    // 426, not 401: the till keeps trading on what it has and updates itself.
    assert.equal((await sync({ deviceId: DEVICE, deviceSecret: secret, clientVersion: "1.4.2" }, gated)).status, 426);
  });

  it("lets a till through when it cannot say what version it is", async () => {
    const { options, secret } = await activated();
    const gated: Options = { ...options, minClientVersion: "2.0.0" };

    assert.equal((await sync({ deviceId: DEVICE, deviceSecret: secret }, gated)).status, 200);
  });
});

describe("admin", () => {
  const TOKEN = "admin-token-for-tests";

  function admin(shop: Partial<Shop> = {}) {
    const { store, options } = setup(shop);
    return { store, options: { ...options, adminToken: TOKEN } as Options };
  }

  it("creates a shop and hands back a code somebody can read out", async () => {
    const { store, options } = admin();

    const reply = await adminSaveShop(
      { shopId: "new-shop", edition: "print", features: ["web-orders"], terminals: 2 },
      `Bearer ${TOKEN}`,
      options,
    );

    assert.equal(reply.status, 200);
    assert.match(reply.body.activationCode as string, /^[0-9A-Z]{4}-[0-9A-Z]{4}$/);
    assert.equal(typeof reply.body.expiresAt, "string");

    const saved = await store.shop("new-shop");
    assert.ok(saved);
    assert.equal(saved.edition, "print");
    assert.deepEqual(saved.features, ["web-orders"]);
    assert.equal(saved.terminals, 2);
  });

  it("mints a code the till then accepts", async () => {
    const { options } = admin();

    const created = await adminSaveShop({ shopId: "new-shop" }, `Bearer ${TOKEN}`, options);
    const code = created.body.activationCode as string;

    const reply = await activate({ deviceId: "a-new-till", activationCode: code }, options);

    assert.equal(reply.status, 200);
    assert.equal(reply.body.shopId, "new-shop");
  });

  /** Re-running it is how a lost code is replaced, and the old one must stop working. */
  it("replaces the code, and the previous one dies", async () => {
    const { options } = admin();

    const first = await adminSaveShop({ shopId: "demo-shop" }, `Bearer ${TOKEN}`, options);
    const second = await adminSaveShop({ shopId: "demo-shop" }, `Bearer ${TOKEN}`, options);

    assert.notEqual(first.body.activationCode, second.body.activationCode);

    const old = await activate(
      { deviceId: DEVICE, activationCode: first.body.activationCode as string },
      options,
    );
    assert.equal(old.status, 401);

    const now = await activate(
      { deviceId: DEVICE, activationCode: second.body.activationCode as string },
      options,
    );
    assert.equal(now.status, 200);
  });

  it("defaults to one terminal on the full till", async () => {
    const { options } = admin();

    const reply = await adminSaveShop({ shopId: "plain" }, `Bearer ${TOKEN}`, options);

    assert.equal(reply.body.edition, "pos");
    assert.equal(reply.body.terminals, 1);
    assert.deepEqual(reply.body.features, []);
  });

  it("refuses a wrong token, and does not care how the header is capitalised", async () => {
    const { options } = admin();

    for (const header of [undefined, "", "Bearer wrong", "wrong", `Basic ${TOKEN}`]) {
      assert.equal((await adminSaveShop({ shopId: "x" }, header, options)).status, 401, String(header));
    }

    assert.equal((await adminSaveShop({ shopId: "x" }, `bearer ${TOKEN}`, options)).status, 200);
  });

  /**
   * With no token configured the endpoint does not exist. A deployment that
   * forgot to set one is closed by accident rather than open by accident.
   */
  it("is not there at all when no token is configured", async () => {
    const { options } = setup();

    const reply = await adminSaveShop({ shopId: "x" }, `Bearer ${TOKEN}`, options);

    assert.equal(reply.status, 404);
    assert.equal(reply.body.error, "no such endpoint");
  });


  it("lists the estate, and says which build each shop is on", async () => {
    const { store, options } = admin();

    const created = await adminSaveShop({ shopId: "demo-shop" }, `Bearer ${TOKEN}`, options);
    await activate(
      { deviceId: DEVICE, activationCode: created.body.activationCode as string, clientVersion: "1.4.2" },
      options,
    );

    const reply = await adminListShops(`Bearer ${TOKEN}`, options);

    assert.equal(reply.status, 200);
    const shops = reply.body.shops as Record<string, unknown>[];
    const demo = shops.find((s) => s.shopId === "demo-shop");

    assert.ok(demo);
    assert.equal(demo.devices, 1);
    assert.deepEqual(demo.clientVersions, ["1.4.2"]);
    assert.equal(typeof demo.lastSeen, "string");
    assert.ok(store);
  });

  /** Only the hash of a code is stored, so a listing could not leak one even by mistake. */
  it("never shows a code, only whether one is live", async () => {
    const { options } = admin();

    const created = await adminSaveShop({ shopId: "demo-shop" }, `Bearer ${TOKEN}`, options);
    const reply = await adminListShops(`Bearer ${TOKEN}`, options);

    const serialised = JSON.stringify(reply.body);
    const code = (created.body.activationCode as string).replace("-", "");

    assert.ok(!serialised.includes(code));
    assert.ok(!serialised.toLowerCase().includes("activationcode"));
  });

  it("guards the listing the same way", async () => {
    const { options } = admin();

    assert.equal((await adminListShops("Bearer wrong", options)).status, 401);
    assert.equal((await adminListShops(`Bearer ${TOKEN}`, setup().options)).status, 404);
  });

  it("refuses a shape it cannot store", async () => {
    const { options } = admin();

    for (const bad of [
      {},
      { shopId: "x", edition: "enterprise" },
      { shopId: "x", terminals: 0 },
      { shopId: "x", terminals: 1.5 },
    ]) {
      assert.equal((await adminSaveShop(bad, `Bearer ${TOKEN}`, options)).status, 400, JSON.stringify(bad));
    }
  });
});

describe("the change log arriving on sync", () => {
  async function activated() {
    const { store, options } = setup();
    const reply = await activate({ deviceId: DEVICE, activationCode: CODE }, options);
    return { store, options, secret: reply.body.deviceSecret as string };
  }

  /** Built the way the till builds one, so the hashes are real. */
  function chain(count: number, from = GENESIS, firstSeq = 1): ChangeEntry[] {
    const out: ChangeEntry[] = [];
    let prev = from;

    for (let i = 0; i < count; i++) {
      const seq = firstSeq + i;
      const draft: ChangeEntry = {
        seq,
        id: `entry-${seq}`,
        terminalId: "till-a",
        entity: "order",
        entityId: `order-${seq}`,
        op: "placed",
        payload: `{"orderNumber":"10${seq}","totalPence":1250}`,
        at: "2026-08-31T19:30:00.0000000+00:00",
        staffId: "wei",
        prevHash: prev,
        hash: "",
      };
      const hash = hashOf(prev, draft);
      out.push({ ...draft, hash });
      prev = hash;
    }

    return out;
  }

  it("stores a batch and says how far the till may move its watermark", async () => {
    const { store, options, secret } = await activated();
    const entries = chain(3);

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);

    assert.equal(reply.status, 200);
    assert.equal(reply.body.syncedThrough, 3);
    assert.equal(store.entries.length, 3);

    // Still an entitlement, because that is the other half of this call.
    assert.equal(typeof reply.body.token, "string");
  });

  it("continues the chain across two calls", async () => {
    const { store, options, secret } = await activated();
    const first = chain(2);

    await sync({ deviceId: DEVICE, deviceSecret: secret, entries: first }, options);

    const second = chain(2, first[1]!.hash, 3);
    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, entries: second }, options);

    assert.equal(reply.body.syncedThrough, 4);
    assert.equal(store.entries.length, 4);
  });

  /**
   * A till whose answer was lost has no choice but to send again. Storing the
   * same entry twice would be a log that disagrees with itself.
   */
  it("a batch sent twice is stored once", async () => {
    const { store, options, secret } = await activated();
    const entries = chain(3);

    await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);
    const again = await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);

    assert.equal(store.entries.length, 3);
    assert.equal(again.body.syncedThrough, 3);
  });

  /**
   * The tampering the chain alone cannot see. Once we hold entries, a batch that
   * does not continue from them says something was removed.
   */
  it("a gap is refused, recorded, and does not move the watermark", async () => {
    const { store, options, secret } = await activated();
    const all = chain(5);

    await sync({ deviceId: DEVICE, deviceSecret: secret, entries: all.slice(0, 2) }, options);

    // Entries 3 and 4 deleted on the till; 5 arrives claiming to follow them.
    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, entries: all.slice(4) }, options);

    assert.equal(reply.status, 200);
    assert.equal(reply.body.syncedThrough, 2);
    assert.match(reply.body.logError as string, /missing, or the log was rewritten/);
    assert.ok(store.broken.has(DEVICE));

    // And the till is still told what it may do — a broken chain is our problem
    // to look at, not a reason to stop a shop trading.
    assert.equal(typeof reply.body.token, "string");
  });

  it("an edited payload is refused", async () => {
    const { store, options, secret } = await activated();
    const entries = chain(2);
    entries[1] = { ...entries[1]!, payload: '{"totalPence":1}' };

    const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);

    assert.match(reply.body.logError as string, /contents were changed/);
    assert.equal(store.entries.length, 0);
  });

  it("re-activating a till does not forget where its chain had got to", async () => {
    const { options, secret } = await activated();
    const entries = chain(2);
    await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);

    // The same machine activates again — a dropped answer, or a repaired install.
    const again = await activate({ deviceId: DEVICE, activationCode: CODE }, options);
    const next = chain(1, entries[1]!.hash, 3);

    const reply = await sync(
      { deviceId: DEVICE, deviceSecret: again.body.deviceSecret, entries: next },
      options,
    );

    assert.equal(reply.body.syncedThrough, 3);
    assert.equal(reply.body.logError, undefined);
  });

  it("no entries is not an error, and moves nothing", async () => {
    const { options, secret } = await activated();

    for (const entries of [undefined, []]) {
      const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);

      assert.equal(reply.status, 200);
      assert.equal(reply.body.syncedThrough, 0);
      assert.equal(reply.body.logError, undefined);
    }
  });

  it("rubbish where a batch should be is reported, not thrown", async () => {
    const { options, secret } = await activated();

    for (const entries of ["not an array", [{ seq: 1 }], [null], [{ seq: 0, id: "x" }]]) {
      const reply = await sync({ deviceId: DEVICE, deviceSecret: secret, entries }, options);

      assert.equal(reply.status, 200);
      assert.match(reply.body.logError as string, /shape we can read/);
    }
  });
});
