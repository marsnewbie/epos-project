import { load } from "./config.ts";
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
  ping: () => store.ping(),
});

server.listen(config.port, () => {
  console.log(
    `ringorder-pos-cloud listening on ${config.port}` +
      (config.minClientVersion ? `, refusing tills older than ${config.minClientVersion}` : ""),
  );
});

for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, () => {
    server.close(() => {
      void store.close().then(() => process.exit(0));
    });
  });
}
