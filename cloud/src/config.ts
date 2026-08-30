/**
 * Everything this service is told by its environment.
 *
 * Read once at startup and validated loudly. A service that discovers at three
 * on a Saturday that its signing key was never set is worse than one that
 * refused to start.
 */

export type Config = {
  port: number;
  databaseUrl: string;
  privateKeyPem: string;
  minClientVersion: string | null;
  adminToken: string | null;
};

/**
 * Accepts the key either as a plain PEM with real newlines or as base64.
 *
 * Both because platform environment editors disagree about multi-line values,
 * and a key mangled by a web form is a failure that looks like a code fault for
 * an hour before anybody suspects the paste.
 */
export function readPrivateKey(raw: string | undefined): string {
  const value = (raw ?? "").trim();
  if (value.length === 0) throw new Error("SIGNING_KEY is not set");

  if (value.includes("BEGIN")) return value.replace(/\\n/g, "\n");

  const decoded = Buffer.from(value, "base64").toString("utf8");
  if (!decoded.includes("BEGIN")) {
    throw new Error("SIGNING_KEY is neither a PEM nor base64 of one");
  }

  return decoded;
}

export function load(env: NodeJS.ProcessEnv = process.env): Config {
  const databaseUrl = (env.DATABASE_URL ?? "").trim();
  if (databaseUrl.length === 0) throw new Error("DATABASE_URL is not set");

  return {
    port: Number(env.PORT ?? 8080),
    databaseUrl,
    privateKeyPem: readPrivateKey(env.SIGNING_KEY),

    // Absent means "answer every till". A floor is set deliberately, on the day
    // an old build genuinely cannot be answered — never as a default, because a
    // default here quietly cuts off whoever has not updated.
    minClientVersion: (env.MIN_CLIENT_VERSION ?? "").trim() || null,

    // Absent closes the admin endpoint rather than opening it. A deployment
    // that forgot to set one answers 404 there, which is the state you want to
    // be in by accident.
    adminToken: (env.ADMIN_TOKEN ?? "").trim() || null,
  };
}
