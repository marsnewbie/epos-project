import pg from "pg";
import {
  hashSecret,
  type ChangeLogRow,
  type Device,
  type Shop,
  type ShopSummary,
  type Store,
} from "./store.ts";

/**
 * The store as it runs in production.
 *
 * One dependency — `pg` — and it is the only one in the service. The AI phone
 * project reaches its database over HTTP and so needs none; Railway's Postgres
 * speaks its own wire protocol and there is no honest way around a driver.
 */
export class PostgresStore implements Store {
  /**
   * Readable so the migration runner can borrow a client. Everything else here
   * goes through the methods; this is the one deliberate seam.
   */
  readonly pool: pg.Pool;
  #pool: pg.Pool;

  constructor(connectionString: string) {
    this.pool = this.#pool = new pg.Pool({
      connectionString,

      // Railway's managed Postgres presents a certificate its own clients trust
      // and node-postgres does not. The connection is still encrypted; what is
      // skipped is chain verification inside Railway's private network.
      ssl: connectionString.includes("localhost") ? false : { rejectUnauthorized: false },
      max: 5,
    });
  }

  /**
   * Looked up by the hash of the code, not by scanning.
   *
   * Hashing first means the plain code never reaches the query log, and the
   * unique partial index makes this a single index probe however many shops
   * exist.
   */
  async shopForActivation(code: string, now: Date): Promise<Shop | null> {
    const { rows } = await this.#pool.query<ShopRow & { activation_expires_at: Date | null }>(
      `SELECT id, edition, features, terminals, activation_expires_at
         FROM shops
        WHERE activation_key_hash = $1`,
      [hashSecret(code)],
    );

    const row = rows[0];
    if (!row) return null;

    // Expiry is checked here rather than in the WHERE clause so that an expired
    // code and a wrong code are the same answer to the caller — there is nothing
    // to learn from the difference.
    if (row.activation_expires_at && row.activation_expires_at <= now) return null;

    return toShop(row);
  }

  async shop(shopId: string): Promise<Shop | null> {
    const { rows } = await this.#pool.query<ShopRow>(
      `SELECT id, edition, features, terminals FROM shops WHERE id = $1`,
      [shopId],
    );

    return rows[0] ? toShop(rows[0]) : null;
  }

  async device(deviceId: string): Promise<Device | null> {
    const { rows } = await this.#pool.query<{
      id: string;
      shop_id: string;
      secret_hash: string;
      chain_head: string | null;
      chain_seq: string | number;
    }>(
      `SELECT id, shop_id, secret_hash, chain_head, chain_seq FROM devices WHERE id = $1`,
      [deviceId],
    );

    const row = rows[0];
    if (!row) return null;

    return {
      id: row.id,
      shopId: row.shop_id,
      secretHash: row.secret_hash,
      chainHead: row.chain_head,
      chainSeq: Number(row.chain_seq),
    };
  }

  async saveDevice(deviceId: string, shopId: string, secretHash: string): Promise<void> {
    // The chain columns are deliberately absent from the UPDATE: re-activating a
    // till must not forget where its chain had got to, or the next batch would
    // look like a rewrite.
    await this.#pool.query(
      `INSERT INTO devices (id, shop_id, secret_hash)
            VALUES ($1, $2, $3)
       ON CONFLICT (id) DO UPDATE
              SET shop_id = excluded.shop_id,
                  secret_hash = excluded.secret_hash`,
      [deviceId, shopId, secretHash],
    );
  }

  /**
   * One transaction for the whole batch and the head it moves to.
   *
   * A head that advanced past entries which failed to insert would refuse every
   * batch after it, and the shop would look tampered with because of a database
   * hiccup.
   */
  async saveChangeLog(
    deviceId: string,
    shopId: string,
    entries: ChangeLogRow[],
    head: string,
    headSeq: number,
  ): Promise<void> {
    const client = await this.#pool.connect();

    try {
      await client.query("BEGIN");

      for (const e of entries) {
        await client.query(
          `INSERT INTO change_log
             (id, device_id, shop_id, seq, terminal_id, entity, entity_id, op,
              payload, at, at_utc, staff_id, prev_hash, hash)
           VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
           ON CONFLICT (id) DO NOTHING`,
          [
            e.id, deviceId, shopId, e.seq, e.terminalId, e.entity, e.entityId, e.op,
            // `payload` and `at` go in verbatim — they are the bytes that were
            // hashed. `at_utc` is the derived one, for querying.
            e.payload, e.at, new Date(e.at), e.staffId, e.prevHash, e.hash,
          ],
        );
      }

      await client.query(
        `UPDATE devices SET chain_head = $2, chain_seq = $3 WHERE id = $1`,
        [deviceId, head, headSeq],
      );

      await client.query("COMMIT");
    } catch (error) {
      await client.query("ROLLBACK");
      throw error;
    } finally {
      client.release();
    }
  }

  async recordChainBroken(deviceId: string, reason: string, at: Date): Promise<void> {
    // Only the first break is kept. A chain that broke once is a thing a person
    // looks at, and overwriting it with a later one loses the event that matters.
    await this.#pool.query(
      `UPDATE devices
          SET chain_broken_at = COALESCE(chain_broken_at, $2),
              chain_broken_reason = COALESCE(chain_broken_reason, $3)
        WHERE id = $1`,
      [deviceId, at, reason],
    );
  }

  async recordSeen(deviceId: string, clientVersion: string | null, at: Date): Promise<void> {
    await this.#pool.query(
      `UPDATE devices SET last_seen = $2, client_version = $3 WHERE id = $1`,
      [deviceId, at, clientVersion],
    );
  }

  async saveShop(shop: Shop, codeHash: string, expiresAt: Date): Promise<void> {
    await this.#pool.query(
      `INSERT INTO shops (id, edition, features, terminals, activation_key_hash, activation_expires_at)
            VALUES ($1, $2, $3, $4, $5, $6)
       ON CONFLICT (id) DO UPDATE
              SET edition = excluded.edition,
                  features = excluded.features,
                  terminals = excluded.terminals,
                  activation_key_hash = excluded.activation_key_hash,
                  activation_expires_at = excluded.activation_expires_at`,
      [shop.id, shop.edition, shop.features, shop.terminals, codeHash, expiresAt],
    );
  }

  /**
   * One query, aggregating in the database.
   *
   * A shop count times a device query would be fine at ten shops and a problem
   * at a thousand, and the shape of the answer is the same either way — so it is
   * written the way it will have to stay.
   */
  async listShops(): Promise<ShopSummary[]> {
    const { rows } = await this.#pool.query<ShopRow & {
      activation_expires_at: Date | null;
      devices: string;
      last_seen: Date | null;
      client_versions: (string | null)[] | null;
    }>(`
      SELECT s.id, s.edition, s.features, s.terminals, s.activation_expires_at,
             COUNT(d.id)                                  AS devices,
             MAX(d.last_seen)                             AS last_seen,
             ARRAY_REMOVE(ARRAY_AGG(DISTINCT d.client_version), NULL) AS client_versions
        FROM shops s
        LEFT JOIN devices d ON d.shop_id = s.id
       GROUP BY s.id
       ORDER BY s.id
    `);

    return rows.map((row) => ({
      ...toShop(row),
      devices: Number(row.devices),
      lastSeen: row.last_seen,
      clientVersions: (row.client_versions ?? []).filter((v): v is string => v !== null),
      activationExpiresAt: row.activation_expires_at,
    }));
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
