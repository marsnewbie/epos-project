import { createHash, randomBytes, timingSafeEqual } from "node:crypto";

/**
 * What the service is allowed to know about a shop.
 *
 * **No orders, no customers, no money.** The till is the system of record and
 * this is an entitlement authority; the first cloud service is where that
 * boundary either holds or starts leaking, so the absence of those columns is
 * the enforcement. Adding one is a decision that belongs in docs/CLOUD.md with
 * a reason, not a migration somebody writes on a Tuesday.
 */
export type Shop = {
  id: string;
  edition: string;
  features: string[];
  terminals: number;
};

export type Device = {
  id: string;
  shopId: string;
  secretHash: string;
};

export interface Store {
  /** The shop this activation key belongs to, or null if the key is wrong. */
  shopForActivation(shopId: string, activationKey: string): Promise<Shop | null>;
  shop(shopId: string): Promise<Shop | null>;
  device(deviceId: string): Promise<Device | null>;

  /**
   * Records a device against a shop with a new secret.
   *
   * **Idempotent by design, and that is the recovery path.** If the network drops
   * after this service answers but before the till stores what it was given, the
   * till has an activation key and no secret — its only way out is to activate
   * again. Refusing a second activation would strand that machine permanently.
   */
  saveDevice(deviceId: string, shopId: string, secretHash: string): Promise<void>;

  /** Last seen, and what build it said it was. This is what makes retiring a protocol version safe. */
  recordSeen(deviceId: string, clientVersion: string | null, at: Date): Promise<void>;
}

/**
 * Hashes a device secret for storage.
 *
 * A single SHA-256, deliberately, where a password would need scrypt or argon2.
 * The difference is entropy: this secret is 32 random bytes that we generate, so
 * there is no dictionary to run and no cost worth imposing on every request. A
 * slow KDF here would defend against nothing and would make a shop's daily
 * refresh measurably slower.
 */
export const hashSecret = (secret: string): string =>
  createHash("sha256").update(secret, "utf8").digest("hex");

/** 32 random bytes. Shown to the till once and stored only as a hash. */
export const newSecret = (): string => randomBytes(32).toString("hex");

/** Constant-time comparison, so a wrong secret does not leak how wrong it was. */
export function secretMatches(offered: string, storedHash: string): boolean {
  const a = Buffer.from(hashSecret(offered), "hex");
  const b = Buffer.from(storedHash, "hex");

  return a.length === b.length && timingSafeEqual(a, b);
}

/** A store that lives in memory, for the tests. Never used by the running service. */
export class MemoryStore implements Store {
  readonly shops = new Map<string, Shop & { activationKeyHash: string | null }>();
  readonly devices = new Map<string, Device>();
  readonly seen = new Map<string, { clientVersion: string | null; at: Date }>();

  async shopForActivation(shopId: string, activationKey: string): Promise<Shop | null> {
    const shop = this.shops.get(shopId);
    if (!shop?.activationKeyHash) return null;

    return secretMatches(activationKey, shop.activationKeyHash) ? shop : null;
  }

  async shop(shopId: string): Promise<Shop | null> {
    return this.shops.get(shopId) ?? null;
  }

  async device(deviceId: string): Promise<Device | null> {
    return this.devices.get(deviceId) ?? null;
  }

  async saveDevice(deviceId: string, shopId: string, secretHash: string): Promise<void> {
    this.devices.set(deviceId, { id: deviceId, shopId, secretHash });
  }

  async recordSeen(deviceId: string, clientVersion: string | null, at: Date): Promise<void> {
    this.seen.set(deviceId, { clientVersion, at });
  }
}
