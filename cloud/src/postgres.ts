import pg from "pg";
import { secretMatches, type Device, type Shop, type Store } from "./store.ts";

/**
 * The store as it runs in production.
 *
 * One dependency — `pg` — and it is the only one in the service. The AI phone
 * project reaches its database over HTTP and so needs none; Railway's Postgres
 * speaks its own wire protocol and there is no honest way around a driver.
 */
export class PostgresStore implements Store {
  #pool: pg.Pool;

  constructor(connectionString: string) {
    this.#pool = new pg.Pool({
      connectionString,

      // Railway's managed Postgres presents a certificate its own clients trust
      // and node-postgres does not. The connection is still encrypted; what is
      // skipped is chain verification inside Railway's private network.
      ssl: connectionString.includes("localhost") ? false : { rejectUnauthorized: false },
      max: 5,
    });
  }

  async shopForActivation(shopId: string, activationKey: string): Promise<Shop | null> {
    const { rows } = await this.#pool.query<ShopRow & { activation_key_hash: string | null }>(
      `SELECT id, edition, features, terminals, activation_key_hash
         FROM shops WHERE id = $1`,
      [shopId],
    );

    const row = rows[0];
    if (!row?.activation_key_hash) return null;

    return secretMatches(activationKey, row.activation_key_hash) ? toShop(row) : null;
  }

  async shop(shopId: string): Promise<Shop | null> {
    const { rows } = await this.#pool.query<ShopRow>(
      `SELECT id, edition, features, terminals FROM shops WHERE id = $1`,
      [shopId],
    );

    return rows[0] ? toShop(rows[0]) : null;
  }

  async device(deviceId: string): Promise<Device | null> {
    const { rows } = await this.#pool.query<{ id: string; shop_id: string; secret_hash: string }>(
      `SELECT id, shop_id, secret_hash FROM devices WHERE id = $1`,
      [deviceId],
    );

    const row = rows[0];
    return row ? { id: row.id, shopId: row.shop_id, secretHash: row.secret_hash } : null;
  }

  async saveDevice(deviceId: string, shopId: string, secretHash: string): Promise<void> {
    await this.#pool.query(
      `INSERT INTO devices (id, shop_id, secret_hash)
            VALUES ($1, $2, $3)
       ON CONFLICT (id) DO UPDATE
              SET shop_id = excluded.shop_id,
                  secret_hash = excluded.secret_hash`,
      [deviceId, shopId, secretHash],
    );
  }

  async recordSeen(deviceId: string, clientVersion: string | null, at: Date): Promise<void> {
    await this.#pool.query(
      `UPDATE devices SET last_seen = $2, client_version = $3 WHERE id = $1`,
      [deviceId, at, clientVersion],
    );
  }

  /** Answers whether the database is reachable, for the health check. */
  async ping(): Promise<void> {
    await this.#pool.query("SELECT 1");
  }

  async close(): Promise<void> {
    await this.#pool.end();
  }
}

type ShopRow = {
  id: string;
  edition: string;
  features: string[] | null;
  terminals: number | string;
};

const toShop = (row: ShopRow): Shop => ({
  id: row.id,
  edition: row.edition,
  features: row.features ?? [],

  // `terminals` is an integer column, but node-postgres hands back some numeric
  // types as strings; coercing here keeps that detail out of the token payload,
  // where a quoted number would be a contract change nobody meant to make.
  terminals: Number(row.terminals),
});
