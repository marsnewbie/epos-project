/**
 * Which builds this service will talk to.
 *
 * The floor exists so that a genuinely breaking change has a way out that does
 * not involve telephoning merchants: the service says "too old", the till keeps
 * trading on its cached entitlement, and the updater deals with it.
 *
 * **Too old to sync is never too old to trade.** That rule lives on the till —
 * see `EntitlementService` — but it is the reason this is a soft refusal with a
 * distinct status code rather than an error.
 */

/** A .NET assembly version: `major.minor.build.revision`, any of the tail parts optional. */
export function parseVersion(raw: string | undefined | null): number[] | null {
  if (typeof raw !== "string") return null;

  const trimmed = raw.trim();
  if (!/^\d+(\.\d+)*$/.test(trimmed)) return null;

  const parts = trimmed.split(".").map(Number);
  return parts.some((n) => !Number.isSafeInteger(n) || n < 0) ? null : parts;
}

/** Negative if `a` is older, positive if newer, zero if the same. Missing parts count as zero. */
export function compareVersions(a: number[], b: number[]): number {
  const length = Math.max(a.length, b.length);

  for (let i = 0; i < length; i++) {
    const diff = (a[i] ?? 0) - (b[i] ?? 0);
    if (diff !== 0) return diff;
  }

  return 0;
}

/**
 * Whether a client is too old to be answered.
 *
 * **An unparseable or absent version is allowed through.** A till that cannot
 * say what it is is far more likely to be one we have not taught to report yet
 * than an attacker, and refusing it would take a shop's entitlement away over a
 * missing string. Refusal is reserved for a version we can read and have
 * deliberately retired.
 */
export function isTooOld(clientVersion: string | undefined | null, minimum: string | undefined | null): boolean {
  const floor = parseVersion(minimum);
  if (floor === null) return false;   // no floor configured

  const client = parseVersion(clientVersion);
  if (client === null) return false;  // unreadable — let it through

  return compareVersions(client, floor) < 0;
}
