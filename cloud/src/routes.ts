import { expiryFrom, format, newCode, normalise } from "./codes.ts";
import { issue, type Entitlement } from "./tokens.ts";
import { hashSecret, newSecret, secretMatches, type Shop, type Store } from "./store.ts";
import { isTooOld } from "./version.ts";

/**
 * The two things a till asks for, as pure functions of a request and a store.
 *
 * No `http` in this file: handlers take a parsed body and return a status and a
 * body, so every rule below is tested without a socket or a database.
 */

export type Request = {
  deviceId?: unknown;
  deviceSecret?: unknown;

  /** The short code someone typed on the till. It alone says which shop this is. */
  activationCode?: unknown;

  clientVersion?: unknown;

  /** Admin only. */
  shopId?: unknown;
  edition?: unknown;
  features?: unknown;
  terminals?: unknown;
};

export type Reply = {
  status: number;
  body: Record<string, unknown>;
};

export type Options = {
  store: Store;
  privateKeyPem: string;
  minClientVersion?: string | null;

  /**
   * Bearer token for the admin endpoint. **Absent disables it entirely** — a
   * deployment that forgot to set one has no admin surface rather than an open
   * one.
   */
  adminToken?: string | null;

  now?: () => Date;
};

const str = (value: unknown): string | null =>
  typeof value === "string" && value.trim().length > 0 ? value.trim() : null;

/** 426, so the till knows to update itself rather than to keep retrying. */
const TOO_OLD = 426;

/**
 * Turns a short activation code into a device secret and a first token.
 *
 * The code is the whole credential: it identifies the shop *and* authorises the
 * enrolment. A till therefore needs to know nothing about itself beyond its own
 * random identifier, which is what lets a person activate one by typing eight
 * characters instead of editing a file.
 *
 * Runs once per installation, and again on the recovery path where a till lost
 * this answer to a dropped connection — see `Store.saveDevice`.
 */
export async function activate(request: Request, options: Options): Promise<Reply> {
  const deviceId = str(request.deviceId);
  const code = normalise(typeof request.activationCode === "string" ? request.activationCode : null);
  const clientVersion = str(request.clientVersion);

  if (!deviceId || !code) {
    return { status: 400, body: { error: "deviceId and activationCode are required" } };
  }

  if (isTooOld(clientVersion, options.minClientVersion)) {
    return { status: TOO_OLD, body: { error: "this till is older than this service will answer" } };
  }

  const now = options.now?.() ?? new Date();
  const shop = await options.store.shopForActivation(code, now);

  // Wrong code and expired code are the same answer. There is nothing useful to
  // learn from the difference, and telling them apart is how a guess becomes an
  // oracle.
  if (!shop) {
    return { status: 401, body: { error: "that code is not recognised" } };
  }

  // A second activation of the same device issues a fresh secret rather than
  // refusing. The alternative strands any till whose connection dropped between
  // our answer and its write.
  const secret = newSecret();
  await options.store.saveDevice(deviceId, shop.id, hashSecret(secret));
  await options.store.recordSeen(deviceId, clientVersion, now);

  return {
    status: 200,
    body: {
      token: issue(entitlementFor(shop, deviceId), options.privateKeyPem, now),
      deviceSecret: secret,

      // Echoed so the till can say "connected to Magic Wok" rather than
      // "connected", which is the difference between a person believing it
      // worked and a person checking.
      shopId: shop.id,
    },
  };
}

/**
 * The recurring pipe.
 *
 * Named `sync` rather than `entitlement` because it is the one endpoint a till
 * calls on a schedule, and order ingest and the change log will arrive in this
 * same answer as additional fields — see docs/CLOUD.md. The till ignores fields
 * it does not know, so adding them breaks nothing that is already installed.
 *
 * **A known device is never refused for commercial reasons.** If a shop stops
 * paying, its row is changed and this returns a token that says so; the till
 * degrades to exactly what we decided it should keep. Refusing outright would
 * give us no control over what happens next and would land the change thirty
 * days later, on a day nobody chose.
 */
export async function sync(request: Request, options: Options): Promise<Reply> {
  const deviceId = str(request.deviceId);
  const deviceSecret = str(request.deviceSecret);
  const clientVersion = str(request.clientVersion);

  if (!deviceId || !deviceSecret) {
    return { status: 400, body: { error: "deviceId and deviceSecret are required" } };
  }

  if (isTooOld(clientVersion, options.minClientVersion)) {
    return { status: TOO_OLD, body: { error: "this till is older than this service will answer" } };
  }

  const device = await options.store.device(deviceId);
  if (!device || !secretMatches(deviceSecret, device.secretHash)) {
    return { status: 401, body: { error: "unknown device" } };
  }

  const shop = await options.store.shop(device.shopId);
  if (!shop) {
    // A device whose shop no longer exists. Deleting a shop is a deliberate act
    // on our side, so this is the one place a known device is turned away.
    return { status: 401, body: { error: "shop no longer exists" } };
  }

  const now = options.now?.() ?? new Date();
  await options.store.recordSeen(deviceId, clientVersion, now);

  return {
    status: 200,
    body: { token: issue(entitlementFor(shop, deviceId), options.privateKeyPem, now) },
  };
}

/**
 * The shop's current grant, bound to the machine asking.
 *
 * The `deviceId` is what stops one shop's token unlocking every install, and it
 * is the easiest field in the whole design to leave out — nothing misbehaves
 * without it until the day somebody copies a token.
 */
function entitlementFor(shop: { id: string; edition: string; features: string[]; terminals: number }, deviceId: string): Entitlement {
  return {
    shopId: shop.id,
    deviceId,
    edition: shop.edition,
    features: shop.features,
    terminals: shop.terminals,
  };
}

/**
 * Creates or updates a shop and mints it a fresh activation code.
 *
 * This exists so that adding a merchant is one command rather than a hand-written
 * `INSERT` pasted into a database console — which is how the first shop was
 * added, and how two tables came to be created with one column each.
 *
 * Re-running it on an existing shop replaces the code. That is the recovery path
 * for a lost one, and the old code stops working the moment this returns.
 */
export async function adminSaveShop(
  request: Request,
  authorisation: string | undefined,
  options: Options,
): Promise<Reply> {
  const refused = refuseAdmin(authorisation, options);
  if (refused) return refused;

  const shopId = str(request.shopId);
  if (!shopId) return { status: 400, body: { error: "shopId is required" } };

  const edition = str(request.edition) ?? "pos";
  if (edition !== "pos" && edition !== "print") {
    return { status: 400, body: { error: `edition must be "pos" or "print"` } };
  }

  const terminals = Number(request.terminals ?? 1);
  if (!Number.isInteger(terminals) || terminals < 1) {
    return { status: 400, body: { error: "terminals must be a whole number of at least 1" } };
  }

  const features = Array.isArray(request.features)
    ? request.features.filter((f): f is string => typeof f === "string")
    : [];

  const shop: Shop = { id: shopId, edition, features, terminals };
  const code = newCode();
  const now = options.now?.() ?? new Date();
  const expiresAt = expiryFrom(now);

  await options.store.saveShop(shop, hashSecret(code), expiresAt);

  return {
    status: 200,
    body: {
      shopId,
      edition,
      features,
      terminals,

      // Shown once. Only the hash is stored, so it cannot be read back — a lost
      // code costs another call to this endpoint and nothing else.
      activationCode: format(code),
      expiresAt: expiresAt.toISOString(),
    },
  };
}

/**
 * The gate on every admin route.
 *
 * Returns a reply when the caller must be turned away, and nothing when they may
 * proceed — so a route that forgets to check reads obviously wrong rather than
 * quietly working.
 */
function refuseAdmin(authorisation: string | undefined, options: Options): Reply | null {
  // No token configured means no admin surface at all. A deployment that forgot
  // to set one is closed rather than open.
  if (!options.adminToken) {
    return { status: 404, body: { error: "no such endpoint" } };
  }

  const offered = (authorisation ?? "").replace(/^Bearer +/i, "");
  if (offered.length === 0 || !secretMatches(offered, hashSecret(options.adminToken))) {
    return { status: 401, body: { error: "unauthorised" } };
  }

  return null;
}

/**
 * Every shop and how its tills are doing.
 *
 * `clientVersions` is the column that matters: it is what turns "has everybody
 * updated?" from a guess into a list, and it is the only thing that ever makes
 * retiring an old protocol version safe.
 */
export async function adminListShops(
  authorisation: string | undefined,
  options: Options,
): Promise<Reply> {
  const refused = refuseAdmin(authorisation, options);
  if (refused) return refused;

  const shops = await options.store.listShops();

  return {
    status: 200,
    body: {
      shops: shops.map((shop) => ({
        shopId: shop.id,
        edition: shop.edition,
        features: shop.features,
        terminals: shop.terminals,
        devices: shop.devices,
        lastSeen: shop.lastSeen?.toISOString() ?? null,
        clientVersions: shop.clientVersions,

        // Whether a code is still live, not what it is — only the hash is
        // stored, so it could not be shown even if it should be.
        activationExpiresAt: shop.activationExpiresAt?.toISOString() ?? null,
      })),
    },
  };
}
