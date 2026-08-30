import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import type pg from "pg";

/**
 * Applies pending migrations at startup.
 *
 * The till has run its own migrations on startup since the first release, for
 * the same reason: a schema step a human has to remember is a schema step that
 * gets forgotten, and the failure arrives as a 500 with no obvious cause rather
 * than as an error at deploy time.
 *
 * The first attempt at this service left it manual and the very first setup
 * created two tables with one column each, through a button in a database
 * console. That is not a mistake worth being able to make twice.
 */

const MIGRATIONS = join(import.meta.dirname, "..", "migrations");

/**
 * A lock number, not a table.
 *
 * Railway can run two instances during a rolling deploy, and both would start
 * migrating at once. `pg_advisory_lock` makes the second wait for the first and
 * then find nothing left to do. A lock *table* would need a migration of its
 * own, which is the same problem one turn earlier.
 */
const LOCK = 8_147_226_001;

export async function migrate(pool: pg.Pool, log: (message: string) => void = console.log): Promise<string[]> {
  const client = await pool.connect();

  try {
    await client.query("SELECT pg_advisory_lock($1)", [LOCK]);

    await client.query(`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        name       TEXT PRIMARY KEY,
        applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
      )
    `);

    const { rows } = await client.query<{ name: string }>("SELECT name FROM schema_migrations");
    const done = new Set(rows.map((r) => r.name));

    const pending = readdirSync(MIGRATIONS)
      .filter((f) => f.endsWith(".sql"))
      .sort()
      .filter((f) => !done.has(f));

    const applied: string[] = [];

    for (const name of pending) {
      const sql = readFileSync(join(MIGRATIONS, name), "utf8");

      // One transaction per migration: a step that fails leaves nothing behind
      // half-applied, and the next start tries it again from the same place.
      await client.query("BEGIN");
      try {
        await client.query(sql);
        await client.query("INSERT INTO schema_migrations (name) VALUES ($1)", [name]);
        await client.query("COMMIT");
      } catch (error) {
        await client.query("ROLLBACK");
        throw new Error(`migration ${name} failed: ${(error as Error).message}`, { cause: error });
      }

      applied.push(name);
      log(`applied ${name}`);
    }

    if (applied.length === 0) log("schema is up to date");

    return applied;
  } finally {
    await client.query("SELECT pg_advisory_unlock($1)", [LOCK]).catch(() => {});
    client.release();
  }
}
