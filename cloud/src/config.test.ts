import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, it } from "node:test";
import { load, readPrivateKey } from "./config.ts";

const pem = readFileSync(
  join(import.meta.dirname, "..", "..", "fixtures", "entitlement", "dev-private.pem"),
  "utf8",
);

describe("readPrivateKey", () => {
  it("takes a plain PEM", () => {
    assert.ok(readPrivateKey(pem).includes("BEGIN"));
  });

  it("takes base64 of one, because environment editors mangle newlines", () => {
    assert.equal(readPrivateKey(Buffer.from(pem, "utf8").toString("base64")).trim(), pem.trim());
  });

  it("repairs the escaped newlines a web form leaves behind", () => {
    const escaped = pem.replace(/\n/g, "\\n");

    assert.equal(readPrivateKey(escaped).trim(), pem.trim());
  });

  /**
   * Loud, at startup. A service that discovers on a Saturday evening that its
   * signing key was never set is worse than one that refused to start.
   */
  it("refuses to start without a usable key", () => {
    assert.throws(() => readPrivateKey(undefined), /not set/);
    assert.throws(() => readPrivateKey("   "), /not set/);
    assert.throws(() => readPrivateKey("bm90LWEta2V5"), /neither a PEM nor base64/);
  });
});

describe("load", () => {
  const base = { DATABASE_URL: "postgres://localhost/x", SIGNING_KEY: pem };

  it("defaults the port and leaves the version floor unset", () => {
    const config = load(base as NodeJS.ProcessEnv);

    assert.equal(config.port, 8080);

    // Absent means "answer every till". A default floor would quietly cut off
    // whoever had not updated.
    assert.equal(config.minClientVersion, null);
  });

  it("refuses to start without a database", () => {
    assert.throws(() => load({ SIGNING_KEY: pem } as NodeJS.ProcessEnv), /DATABASE_URL/);
  });

  it("reads a floor when one is deliberately set", () => {
    assert.equal(load({ ...base, MIN_CLIENT_VERSION: "2.0.0" } as NodeJS.ProcessEnv).minClientVersion, "2.0.0");
  });
});
