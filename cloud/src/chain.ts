import { createHash } from "node:crypto";

/**
 * The till's change-log chain, verified here.
 *
 * This is a **reimplementation of `ChangeChain` in `RingOrder.Epos.Domain`**, and
 * the two must agree byte for byte. `fixtures/change-log` holds entries with the
 * hashes they are supposed to produce, and both languages recompute them — if
 * both agree with the file, they agree with each other.
 *
 * Verifying here is the whole reason a shop's log is sent at all. The chain
 * makes an alteration visible; sending it makes a **truncated tail** visible
 * too, which is the one tampering the chain alone cannot see, because deleting
 * the newest entry leaves nothing behind to disagree with it.
 */

/** What the first entry ever written chains from. Sixty-four zeros. */
export const GENESIS = "0".repeat(64);

export type ChangeEntry = {
  seq: number;
  id: string;
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

/**
 * The exact bytes that get hashed.
 *
 * Each field is its UTF-8 **byte** length, a colon, then the field, preceded by
 * a pipe. Length-prefixed rather than joined with a separator because a payload
 * is arbitrary JSON, and any character chosen as a delimiter is one somebody can
 * put inside a field to make two different entries hash the same.
 *
 * **Never change this.** Every chain ever written is verifiable only by the
 * exact function that wrote it.
 */
export function canonical(prevHash: string, entry: ChangeEntry): string {
  const field = (value: string) => `|${Buffer.byteLength(value, "utf8")}:${value}`;

  return (
    prevHash +
    field(entry.id) +
    field(entry.terminalId) +
    field(entry.entity) +
    field(entry.entityId) +
    field(entry.op) +
    field(timestamp(entry.at)) +
    field(entry.staffId ?? "") +
    field(entry.payload)
  );
}

/**
 * The one spelling of an instant the chain understands, and it is .NET's, not
 * JavaScript's.
 *
 * `DateTimeOffset.ToUniversalTime().ToString("o")` produces
 * `2026-08-31T19:30:00.0000000+00:00` — **seven fractional digits and a numeric
 * offset**. `Date.toISOString()` produces three digits and a `Z`. They are the
 * same instant and a different string, so hashing the JavaScript spelling would
 * make every entry ever written look tampered with.
 *
 * Checked against the real implementation rather than assumed; the fixtures hold
 * it.
 */
export function timestamp(at: string): string {
  const parsed = Date.parse(at);
  if (Number.isNaN(parsed)) return at;

  // From the original string: Date cannot hold more than milliseconds, and .NET
  // writes ticks.
  const fraction = /\.(\d+)/.exec(at)?.[1] ?? "";
  const ticks = (fraction + "0000000").slice(0, 7);

  const utc = new Date(parsed).toISOString().replace(/\.\d+Z$/, "");
  return `${utc}.${ticks}+00:00`;
}

export const hashOf = (prevHash: string, entry: ChangeEntry): string =>
  createHash("sha256").update(canonical(prevHash, entry), "utf8").digest("hex");

export type ChainCheck =
  | { ok: true; last: string; lastSeq: number }
  | { ok: false; brokenAt: number; reason: string };

/**
 * Checks a batch of entries continues from what we already hold.
 *
 * `expectedFirstPrev` is the hash of the last entry stored for this device, or
 * null the first time we hear from one. **Null accepts whatever the batch
 * begins with**: a till may have been trading before it was ever activated, so
 * our first sight of its chain is legitimately part-way along. From then on,
 * continuity is required.
 */
export function verify(entries: ChangeEntry[], expectedFirstPrev: string | null): ChainCheck {
  let expected = expectedFirstPrev;
  let last = expectedFirstPrev ?? GENESIS;
  let lastSeq = 0;

  for (const entry of entries) {
    if (expected !== null && entry.prevHash !== expected) {
      return {
        ok: false,
        brokenAt: entry.seq,
        reason: `entry ${entry.seq} follows ${expected.slice(0, 12)} but arrived claiming ${entry.prevHash.slice(0, 12)} — entries are missing, or the log was rewritten`,
      };
    }

    const recomputed = hashOf(entry.prevHash, entry);
    if (recomputed !== entry.hash) {
      return {
        ok: false,
        brokenAt: entry.seq,
        reason: `entry ${entry.seq} hashes to ${recomputed.slice(0, 12)} but claims ${entry.hash.slice(0, 12)} — its contents were changed after it was written`,
      };
    }

    if (lastSeq !== 0 && entry.seq <= lastSeq) {
      return { ok: false, brokenAt: entry.seq, reason: `entry ${entry.seq} arrived after ${lastSeq}; a batch must be in order` };
    }

    expected = entry.hash;
    last = entry.hash;
    lastSeq = entry.seq;
  }

  return { ok: true, last, lastSeq };
}
