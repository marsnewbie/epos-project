import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { canonical, GENESIS, hashOf, timestamp, verify, type ChangeEntry } from "./chain.ts";

/**
 * These constants were **printed by the C# implementation**, not worked out
 * here. That is the point: the two are separate implementations of one format,
 * and a test that only checks this file against itself proves nothing about
 * whether a till's log can be read.
 *
 * The first attempt at `timestamp` produced a `Z` where .NET produces `+00:00`.
 * Both spell the same instant; only one hashes to what the till wrote, and
 * without this every entry from every shop would have looked tampered with.
 */
const FROM_CSHARP = {
  timestamp: "2026-08-31T19:30:00.0000000+00:00",
  canonical:
    "0000000000000000000000000000000000000000000000000000000000000000" +
    '|6:abc123|6:till-a|5:order|7:order-1|6:placed' +
    '|33:2026-08-31T19:30:00.0000000+00:00|3:wei|19:{"totalPence":1250}',
  hash: "70f193c9f55ffdceb935de6ec8237450972a9912ac04c10af506d48903a493cb",
};

const entry = (over: Partial<ChangeEntry> = {}): ChangeEntry => ({
  seq: 1,
  id: "abc123",
  terminalId: "till-a",
  entity: "order",
  entityId: "order-1",
  op: "placed",
  payload: '{"totalPence":1250}',
  at: "2026-08-31T19:30:00.0000000+00:00",
  staffId: "wei",
  prevHash: GENESIS,
  hash: FROM_CSHARP.hash,
  ...over,
});

describe("the canonical form", () => {
  it("is byte for byte what the till hashed", () => {
    assert.equal(canonical(GENESIS, entry()), FROM_CSHARP.canonical);
    assert.equal(hashOf(GENESIS, entry()), FROM_CSHARP.hash);
  });

  /**
   * .NET writes seven fractional digits and a numeric offset. JavaScript writes
   * three and a `Z`. Getting this wrong is silent and total.
   */
  it("spells an instant the way .NET does, not the way JavaScript does", () => {
    assert.equal(timestamp("2026-08-31T19:30:00.0000000+00:00"), FROM_CSHARP.timestamp);
    assert.equal(timestamp("2026-08-31T20:30:00.0000000+01:00"), FROM_CSHARP.timestamp);
    assert.equal(timestamp("2026-08-31T19:30:00Z"), FROM_CSHARP.timestamp);

    assert.ok(!timestamp("2026-08-31T19:30:00Z").endsWith("Z"));
  });

  it("cannot be fooled by a field containing the separator", () => {
    const a = entry({ entity: "order|payment", entityId: "42" });
    const b = entry({ entity: "order", entityId: "payment|42" });

    assert.notEqual(hashOf(GENESIS, a), hashOf(GENESIS, b));
  });
});

describe("verify", () => {
  /** Two entries whose hashes are computed the way the till would. */
  function chain(count: number): ChangeEntry[] {
    const out: ChangeEntry[] = [];
    let prev = GENESIS;

    for (let seq = 1; seq <= count; seq++) {
      const draft = entry({ seq, id: `id-${seq}`, entityId: `order-${seq}`, prevHash: prev, hash: "" });
      const hash = hashOf(prev, draft);
      out.push({ ...draft, hash });
      prev = hash;
    }

    return out;
  }

  it("accepts a chain that continues from what we hold", () => {
    const entries = chain(3);

    const first = verify(entries, null);
    assert.ok(first.ok);
    assert.equal(first.lastSeq, 3);
    assert.equal(first.last, entries[2]!.hash);

    const next = chain(4).slice(3);
    assert.ok(verify(next, entries[2]!.hash).ok);
  });

  /**
   * A till may have been trading before it was ever activated, so our first
   * sight of its chain is legitimately part-way along.
   */
  it("accepts whatever the very first batch begins with", () => {
    const entries = chain(5).slice(2);

    assert.ok(verify(entries, null).ok);
  });

  /**
   * The tampering the chain alone cannot see. Once entries are here, a till
   * that later sends a batch that does not continue from what we hold is
   * telling us something was removed.
   */
  it("reports a batch that does not continue from what we hold", () => {
    const entries = chain(4);
    const result = verify(entries.slice(2), entries[0]!.hash);

    assert.ok(!result.ok);
    assert.match(result.reason, /missing, or the log was rewritten/);
  });

  it("reports an entry whose contents were changed", () => {
    const entries = chain(2);
    entries[1] = { ...entries[1]!, payload: '{"totalPence":250}' };

    const result = verify(entries, null);

    assert.ok(!result.ok);
    assert.equal(result.brokenAt, 2);
    assert.match(result.reason, /contents were changed/);
  });

  it("refuses a batch that is out of order", () => {
    const entries = chain(2);

    assert.ok(!verify([entries[1]!, entries[0]!], null).ok);
  });

  it("an empty batch changes nothing", () => {
    const result = verify([], "abc");

    assert.ok(result.ok);
    assert.equal(result.last, "abc");
    assert.equal(result.lastSeq, 0);
  });
});
