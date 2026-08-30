import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { CODE_LIFETIME_DAYS, expiryFrom, format, newCode, normalise } from "./codes.ts";

describe("newCode", () => {
  it("is eight characters a person can read down a telephone", () => {
    for (let i = 0; i < 200; i++) {
      const code = newCode();

      assert.equal(code.length, 8);
      assert.match(code, /^[0-9A-HJKMNP-TV-Z]{8}$/);
    }
  });

  /**
   * `I`, `L`, `O` and `U` are absent on purpose: the first three come back as
   * 1, 1 and 0 over a bad line, and leaving out the fourth is what stops a
   * random code spelling something a merchant would rather not read out.
   */
  it("never contains a character that is read back as another", () => {
    const codes = Array.from({ length: 500 }, newCode).join("");

    for (const confusable of ["I", "L", "O", "U"]) {
      assert.ok(!codes.includes(confusable), `${confusable} should not appear`);
    }
  });

  it("does not repeat itself", () => {
    const seen = new Set(Array.from({ length: 500 }, newCode));

    assert.equal(seen.size, 500);
  });
});

describe("format", () => {
  it("groups the halves, because an eight-character run is read back wrong", () => {
    assert.equal(format("K7M2P9QR"), "K7M2-P9QR");
  });
});

describe("normalise", () => {
  it("takes a code however it was typed", () => {
    for (const typed of ["K7M2P9QR", "k7m2p9qr", "K7M2-P9QR", " k7m2 p9qr ", "K7M2_P9QR\n", "k7m2.p9qr"]) {
      assert.equal(normalise(typed), "K7M2P9QR", typed);
    }
  });

  /** Crockford's substitutions: over a telephone `I` is a one and `O` is a zero. */
  it("forgives the letters people hear as digits", () => {
    assert.equal(normalise("IOZERO1L"), "10ZER011");
    assert.equal(normalise("iozero1l"), "10ZER011");
  });

  it("refuses anything that is not a code", () => {
    for (const bad of ["", "short", "TOOMANYCHARS", "!!!!!!!!", "K7M2P9Q", null, undefined, 12345678]) {
      assert.equal(normalise(bad as string), null, String(bad));
    }
  });

  /**
   * `U` is not in the alphabet, so a code containing one was never issued by us
   * and must not be normalised into something that was.
   */
  it("does not quietly turn an impossible code into a possible one", () => {
    assert.equal(normalise("UUUUUUUU"), null);
  });
});

describe("expiry", () => {
  /**
   * A short code is a weaker secret than a long one, and the honest way to pay
   * for that is an expiry rather than more characters nobody can type.
   */
  it("is seven days out", () => {
    const now = new Date("2026-08-31T10:00:00.000Z");

    assert.equal(CODE_LIFETIME_DAYS, 7);
    assert.equal(expiryFrom(now).toISOString(), "2026-09-07T10:00:00.000Z");
  });
});
