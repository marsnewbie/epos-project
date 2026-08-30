import { issue, type Entitlement } from "./tokens.ts";
import { hashSecret, newSecret, secretMatches, type Store } from "./store.ts";
import { isTooOld } from "./version.ts";

/**
 * The two things a till asks for, as pure functions of a request and a store.
 *
 * No `http` in this file: handlers take a parsed body and return a status and a
 * body, so every rule below is tested without a socket or a database.
 */

export type Request = {
  shopId?: unknown;
  deviceId?: unknown;
  deviceSecret?: unknown;
  activationKey?: unknown;
  clientVersion?: unknown;
};

export type Reply = {
  status: number;
  body: Record<string, unknown>;
};

export type Options = {
  store: Store;
  privateKeyPem: string;
  minClientVersion?: string | null;
  now?: () => Date;
};

const str = (value: unknown): string | null =>
  typeof value === "string" && value.trim().length > 0 ? value.trim() : null;

/** 426, so the till knows to update itself rather than to keep retrying. */
const TOO_OLD = 426;

/**
 * Exchanges a one-time activation key for a device secret and a first token.
 *
 * Runs once per installation, and again on the recovery path where a till lost
 * our answer to a dropped connection — see `Store.saveDevice`.
 */
export async function activate(request: Request, options: Options): Promise<Reply> {
  const shopId = str(request.shopId);
  const deviceId = str(request.deviceId);
  const activationKey = str(request.activationKey);
  const clientVersion = str(request.clientVersion);

  if (!shopId || !deviceId || !activationKey) {
    return { status: 400, body: { error: "shopId, deviceId and activationKey are required" } };
  }

  if (isTooOld(clientVersion, options.minClientVersion)) {
    return { status: TOO_OLD, body: { error: "this till is older than this service will answer" } };
  }

  const shop = await options.store.shopForActivation(shopId, activationKey);
  if (!shop) {
    return { status: 401, body: { error: "unknown shop or activation key" } };
  }

  // A second activation of the same device issues a fresh secret rather than
  // refusing. The alternative strands any till whose connection dropped between
  // our answer and its write.
  const secret = newSecret();
  await options.store.saveDevice(deviceId, shop.id, hashSecret(secret));

  const now = options.now?.() ?? new Date();
  await options.store.recordSeen(deviceId, clientVersion, now);

  return {
    status: 200,
    body: {
      token: issue(entitlementFor(shop, deviceId), options.privateKeyPem, now),
      deviceSecret: secret,
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
