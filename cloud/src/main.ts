import { load } from "./config.ts";
import { migrate } from "./migrate.ts";
import { PostgresStore } from "./postgres.ts";
import { createApp } from "./server.ts";

/**
 * The entry point, and the only file that knows about Postgres.
 *
 * Everything with a rule in it lives in `routes.ts` and `server.ts`, which take
 * a store and are tested against an in-memory one. This file is wiring.
 */

const config = load();
const store = new PostgresStore(config.databaseUrl);

const server = createApp({
  store,
  privateKeyPem: config.privateKeyPem,
  minClientVersion: config.minClientVersion,
  adminToken: config.adminToken,
  ping: () => store.ping(),
});

// Before the port opens, so a deploy that cannot migrate fails as a deploy
// rather than as a 500 an hour later that nobody connects to the schema.
try {
  await migrate(store.pool);
} catch (error) {
  console.error("could not migrate:", error);
  process.exit(1);
}

server.listen(config.port, () => {
  console.log(
    `ringorder-pos-cloud listening on ${config.port}` +
      (config.minClientVersion ? `, refusing tills older than ${config.minClientVersion}` : "") +
      (config.adminToken ? "" : ", admin endpoint closed (no ADMIN_TOKEN)"),
  );
});

for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, () => {
    server.close(() => {
      void store.close().then(() => process.exit(0));
    });
  });
}
