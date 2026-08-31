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

  /** Where this till's chain had got to when we last heard from it. */
  chainHead: string | null;
  chainSeq: number;
};

export interface Store {
  /**
   * The shop an activation code belongs to, or null if it is wrong or expired.
   *
   * **The code alone identifies the shop.** A till therefore does not have to be
   * told which shop it belongs to before it can activate, which is exactly what
   * made the old design need a file edited by hand for every merchant.
   */
  shopForActivation(code: string, now: Date): Promise<Shop | null>;
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

  /**
   * Creates or updates a shop and gives it a fresh activation code.
   *
   * Used only by the admin endpoint. Re-running it on an existing shop is how a
   * lost code is replaced — the old one stops working the moment this returns,
   * which is the point.
   */
  saveShop(shop: Shop, codeHash: string, expiresAt: Date): Promise<void>;

  /**
   * Every shop, with how its tills are doing.
   *
   * The two questions this service exists to be able to answer about the estate:
   * who has gone quiet, and who is still on an old build. Without the second one
   * "has everybody updated?" is a guess, and an old protocol version can never
   * safely be retired.
   */
  listShops(): Promise<ShopSummary[]>;

  /**
   * Stores a batch of change-log entries and moves the device's chain head on.
   *
   * One transaction: a chain head that advanced past entries which failed to
   * insert would refuse every batch after it, and the shop would look tampered
   * with because of a database hiccup.
   *
   * Entries already held are ignored rather than rejected — a till whose answer
   * was lost on the way back has no choice but to send them again.
   */
  saveChangeLog(
    deviceId: string,
    shopId: string,
    entries: ChangeLogRow[],
    head: string,
    headSeq: number,
  ): Promise<void>;

  /** Records that a batch did not add up. Never cleared automatically — see the migration. */
  recordChainBroken(deviceId: string, reason: string, at: Date): Promise<void>;
}

export type ChangeLogRow = {
  id: string;
  seq: number;
  terminalId: string;
  entity: string;
  entityId: string;
  op: string;
  payload: string;
  at: string;
  staffId: string | null;
  prevHash: string;
  hash: string;
};

export type ShopSummary = Shop & {
  devices: number;
  lastSeen: Date | null;
  clientVersions: string[];
  activationExpiresAt: Date | null;
};

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
  readonly shops = new Map<string, Shop & {
    activationKeyHash: string | null;
    activationExpiresAt?: Date | null;
  }>();
  readonly devices = new Map<string, Device>();
  readonly seen = new Map<string, { clientVersion: string | null; at: Date }>();
  readonly entries: ChangeLogRow[] = [];
  readonly broken = new Map<string, { reason: string; at: Date }>();

  async shopForActivation(code: string, now: Date): Promise<Shop | null> {
    for (const shop of this.shops.values()) {
      if (!shop.activationKeyHash || !secretMatches(code, shop.activationKeyHash)) continue;

      if (shop.activationExpiresAt && shop.activationExpiresAt <= now) return null;
      return shop;
    }

    return null;
  }

  async shop(shopId: string): Promise<Shop | null> {
    return this.shops.get(shopId) ?? null;
  }

  async device(deviceId: string): Promise<Device | null> {
    return this.devices.get(deviceId) ?? null;
  }

  async saveDevice(deviceId: string, shopId: string, secretHash: string): Promise<void> {
    const existing = this.devices.get(deviceId);

    this.devices.set(deviceId, {
      id: deviceId,
      shopId,
      secretHash,

      // Re-activation must not forget where the chain had got to, or the next
      // batch would look like a rewrite.
      chainHead: existing?.chainHead ?? null,
      chainSeq: existing?.chainSeq ?? 0,
    });
  }

  async recordSeen(deviceId: string, clientVersion: string | null, at: Date): Promise<void> {
    this.seen.set(deviceId, { clientVersion, at });
  }

  async saveShop(shop: Shop, codeHash: string, expiresAt: Date): Promise<void> {
    this.shops.set(shop.id, { ...shop, activationKeyHash: codeHash, activationExpiresAt: expiresAt });
  }

  async saveChangeLog(
    deviceId: string,
    _shopId: string,
    entries: ChangeLogRow[],
    head: string,
    headSeq: number,
  ): Promise<void> {
    for (const entry of entries) {
      if (!this.entries.some((e) => e.id === entry.id)) this.entries.push(entry);
    }

    const device = this.devices.get(deviceId);
    if (device) this.devices.set(deviceId, { ...device, chainHead: head, chainSeq: headSeq });
  }

  async recordChainBroken(deviceId: string, reason: string, at: Date): Promise<void> {
    this.broken.set(deviceId, { reason, at });
  }

  async listShops(): Promise<ShopSummary[]> {
    return [...this.shops.values()].map((shop) => {
      const devices = [...this.devices.values()].filter((d) => d.shopId === shop.id);
      const seen = devices.map((d) => this.seen.get(d.id)).filter((s) => s !== undefined);

      return {
        id: shop.id,
        edition: shop.edition,
        features: shop.features,
        terminals: shop.terminals,
        devices: devices.length,
        lastSeen: seen.reduce<Date | null>((a, s) => (!a || s.at > a ? s.at : a), null),
        clientVersions: [...new Set(seen.map((s) => s.clientVersion).filter((v) => v !== null))],
        activationExpiresAt: shop.activationExpiresAt ?? null,
      };
    });
  }
}
