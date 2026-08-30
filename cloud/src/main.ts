import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { load } from "./config.ts";
import { PostgresStore } from "./postgres.ts";
import { activate, sync, type Options, type Reply } from "./routes.ts";

/**
 * The service.
 *
 * `node:http` and no framework. Three routes do not earn a router, and every
 * dependency here is one more thing to keep patched on a service whose whole job
 * is to be boring and available.
 */

const config = load();
const store = new PostgresStore(config.databaseUrl);

const options: Options = {
  store,
  privateKeyPem: config.privateKeyPem,
  minClientVersion: config.minClientVersion,
};

/** Refuses anything unreasonable before it is parsed. A till's request is a few hundred bytes. */
const MAX_BODY_BYTES = 8 * 1024;

async function readJson(req: IncomingMessage): Promise<Record<string, unknown> | null> {
  const chunks: Buffer[] = [];
  let size = 0;

  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_BODY_BYTES) return null;
    chunks.push(chunk as Buffer);
  }

  try {
    const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    return parsed !== null && typeof parsed === "object" ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function send(res: ServerResponse, reply: Reply): void {
  const body = JSON.stringify(reply.body);
  res.writeHead(reply.status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),

    // Nothing here may sit in a proxy. An entitlement is per-device and a cached
    // one would hand a shop somebody else's plan.
    "cache-control": "no-store",
  });
  res.end(body);
}

const server = createServer((req, res) => {
  void handle(req, res).catch((error: unknown) => {
    // Logged rather than returned. What went wrong inside is not a till's
    // business, and a stack trace in a response body is how internals leak.
    console.error("unhandled", error);
    if (!res.headersSent) send(res, { status: 500, body: { error: "internal error" } });
  });
});

async function handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const path = new URL(req.url ?? "/", "http://localhost").pathname;

  if (req.method === "GET" && (path === "/healthz" || path === "/")) {
    try {
      await store.ping();
      send(res, { status: 200, body: { ok: true } });
    } catch {
      send(res, { status: 503, body: { ok: false, error: "database unreachable" } });
    }
    return;
  }

  if (req.method !== "POST") {
    send(res, { status: 405, body: { error: "method not allowed" } });
    return;
  }

  const body = await readJson(req);
  if (body === null) {
    send(res, { status: 400, body: { error: "expected a small JSON object" } });
    return;
  }

  switch (path) {
    case "/v1/activate":
      send(res, await activate(body, options));
      return;

    case "/v1/sync":
      send(res, await sync(body, options));
      return;

    default:
      send(res, { status: 404, body: { error: "no such endpoint" } });
  }
}

server.listen(config.port, () => {
  console.log(
    `ringorder-pos-cloud listening on ${config.port}` +
      (config.minClientVersion ? `, refusing tills older than ${config.minClientVersion}` : ""),
  );
});

for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, () => {
    server.close(() => void store.close().then(() => process.exit(0)));
  });
}
