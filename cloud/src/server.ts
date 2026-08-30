import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { activate, sync, type Options, type Reply } from "./routes.ts";

/**
 * The HTTP skin over the two handlers.
 *
 * `node:http` and no framework. Three routes do not earn a router, and every
 * dependency is one more thing to keep patched on a service whose entire job is
 * to be boring and available.
 *
 * Separated from `main.ts` so the transport rules — the body cap, malformed
 * JSON, method gating, the cache header — are testable without a database or a
 * signing key in the environment.
 */

export type ServerOptions = Options & {
  /** Answers the health check. Throwing means unhealthy. */
  ping: () => Promise<void>;
};

/** A till's request is a few hundred bytes; anything larger is refused before it is parsed. */
export const MAX_BODY_BYTES = 8 * 1024;

async function readJson(req: IncomingMessage): Promise<Record<string, unknown> | null> {
  const chunks: Buffer[] = [];
  let size = 0;

  for await (const chunk of req) {
    size += (chunk as Buffer).length;
    if (size > MAX_BODY_BYTES) return null;
    chunks.push(chunk as Buffer);
  }

  try {
    const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString("utf8"));

    // Arrays and bare values are not requests. Rejecting them here keeps the
    // handlers free of "is this even an object" checks.
    return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function send(res: ServerResponse, reply: Reply): void {
  const body = JSON.stringify(reply.body);

  res.writeHead(reply.status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),

    // Nothing here may sit in a proxy. An entitlement is per-device, and a
    // cached one would hand a shop somebody else's plan.
    "cache-control": "no-store",
  });
  res.end(body);
}

async function route(req: IncomingMessage, res: ServerResponse, options: ServerOptions): Promise<void> {
  const path = new URL(req.url ?? "/", "http://localhost").pathname;

  if (req.method === "GET" && (path === "/healthz" || path === "/")) {
    try {
      await options.ping();
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

export function createApp(options: ServerOptions): Server {
  return createServer((req, res) => {
    void route(req, res, options).catch((error: unknown) => {
      // Logged, never returned. What went wrong inside is not a till's business,
      // and a stack trace in a response body is how internals leak.
      console.error("unhandled", error);
      if (!res.headersSent) send(res, { status: 500, body: { error: "internal error" } });
    });
  });
}
