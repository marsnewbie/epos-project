import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { compareVersions, isTooOld, parseVersion } from "./version.ts";

describe("parseVersion", () => {
  it("reads a .NET assembly version", () => {
    assert.deepEqual(parseVersion("1.4.2.0"), [1, 4, 2, 0]);
    assert.deepEqual(parseVersion("  2.0  "), [2, 0]);
    assert.deepEqual(parseVersion("7"), [7]);
  });

  it("refuses anything it cannot read", () => {
    for (const bad of ["", "dev", "1.4.2-beta", "v1.4", "1..2", "-1.0", null, undefined, "1.2.3.4.5.x"]) {
      assert.equal(parseVersion(bad), null, `expected null for ${String(bad)}`);
    }
  });
});

describe("compareVersions", () => {
  it("compares part by part, treating missing parts as zero", () => {
    assert.ok(compareVersions([1, 4], [1, 5]) < 0);
    assert.ok(compareVersions([2, 0], [1, 9, 9]) > 0);
    assert.equal(compareVersions([1, 4, 0], [1, 4]), 0);
    assert.ok(compareVersions([1, 4], [1, 4, 1]) < 0);
  });

  it("does not compare parts as text", () => {
    // The classic: "10" sorts before "9" as a string.
    assert.ok(compareVersions([1, 10], [1, 9]) > 0);
  });
});

describe("isTooOld", () => {
  it("refuses only what is genuinely below the floor", () => {
    assert.ok(isTooOld("1.4.2", "2.0.0"));
    assert.ok(!isTooOld("2.0.0", "2.0.0"));
    assert.ok(!isTooOld("2.1", "2.0.0"));
  });

  it("answers every till when no floor is set", () => {
    for (const floor of [null, undefined, "", "   "]) {
      assert.ok(!isTooOld("0.0.1", floor));
    }
  });

  /**
   * A till that cannot say what it is is far more likely to be one we have not
   * taught to report yet than an attacker. Refusing it would take a shop's
   * entitlement away over a missing string, so refusal is reserved for a version
   * we can read and have deliberately retired.
   */
  it("lets an unreadable version through rather than cutting a shop off", () => {
    for (const version of [null, undefined, "", "dev", "unknown"]) {
      assert.ok(!isTooOld(version, "2.0.0"));
    }
  });
});
