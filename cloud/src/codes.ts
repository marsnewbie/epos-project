import { randomInt } from "node:crypto";

/**
 * Activation codes a person can read down a telephone and type on a till.
 *
 * The previous design put a 32-character key in a file that somebody edited by
 * hand for every shop. That is a provisioning engineer's answer to a product
 * question, and it does not survive contact with a merchant: they will not edit
 * JSON, and neither should anyone during a Saturday install.
 *
 * So: eight characters, shown as `XXXX-XXXX`, typed into a box on the till.
 * The same shape as every other device that has ever been paired — a card
 * terminal, a set-top box, a smart speaker.
 */

/**
 * Crockford's base32. No `I`, `L`, `O` or `U` — the first three because they
 * are read back as 1, 1 and 0 over a bad phone line, and `U` because excluding
 * it is what stops a random code spelling something unfortunate.
 */
const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

const LENGTH = 8;

/**
 * How long a code stays usable.
 *
 * A short code is a weaker secret than a long one, and the honest way to pay for
 * that is with an expiry rather than with more characters nobody can type. Seven
 * days covers "we are installing next week" and does not leave a live code in an
 * email forever.
 */
export const CODE_LIFETIME_DAYS = 7;

/** `randomInt` rather than `Math.random`: this is a credential, however short. */
export function newCode(): string {
  let out = "";
  for (let i = 0; i < LENGTH; i++) out += ALPHABET[randomInt(ALPHABET.length)];
  return out;
}

/** `ABCD-EFGH` — grouped because an eight-character run is read back wrong. */
export const format = (code: string): string => `${code.slice(0, 4)}-${code.slice(4)}`;

/**
 * Accepts what a human actually types.
 *
 * Lower case, missing or extra dashes, spaces, and the substitutions Crockford
 * exists to absorb: `I` and `L` are read as `1`, `O` as `0`. Returns null for
 * anything that still is not a code, so a typo is refused before it becomes a
 * failed lookup that looks like a wrong code.
 */
export function normalise(raw: string | null | undefined): string | null {
  if (typeof raw !== "string") return null;

  const cleaned = raw
    .toUpperCase()
    .replace(/[^0-9A-Z]/g, "")
    .replace(/[IL]/g, "1")
    .replace(/O/g, "0");

  if (cleaned.length !== LENGTH) return null;

  return [...cleaned].every((c) => ALPHABET.includes(c)) ? cleaned : null;
}

export function expiryFrom(now: Date): Date {
  return new Date(now.getTime() + CODE_LIFETIME_DAYS * 24 * 60 * 60 * 1000);
}
