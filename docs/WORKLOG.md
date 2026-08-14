# Worklog

Running record of the rebuild: what changed, why, and anything that touched real
data. Newest entry at the bottom. Decisions that outlive a single step belong in
the doc they govern — this file is the trail, not the specification.

---

## 2026-08-14 — Foundation, step 1: freeze the existing data

The development database (`%APPDATA%\MagicWok.Epos\data.sqlite`) held the only
copy of a menu we entered and checked by hand: 21 categories, 179 dishes, 28
option-group instances including conditional (`showWhen`) chains, 14 quick
notes. Nothing about the rebuild may lose it, so it was exported before any code
moved.

**Exported** to `shops/demo/shop.ringpos.json` — the shop configuration bundle
format the product will use from here on. Reconciliation between the database
and the bundle:

| | Legacy DB | Bundle |
|---|---|---|
| Categories | 21 | 21 |
| Dishes | 179 | 170 shop + 9 test fixture |
| Sum of all prices | £1237.58 | £1237.58 |
| Option-group instances | 28 | 28 links |
| Quick notes | 14 | 14 |
| Unresolved `showWhen` | — | none |

**Three deliberate changes** made during the export:

1. **Money is now integer pence.** `REAL` columns holding money drift — a till
   that cannot add up is not a till. Conversion rounds half-up off the decimal
   string, never off a float.

2. **Option groups became a shared catalogue.** Each dish used to carry its own
   JSON copy, so a group used by several dishes had to be edited several times.
   Only one true duplicate existed (`Choose Protein`, four identical copies
   across Thai dishes) — it is now one group with four references.

   This surfaced a real defect in the old data: **five** Thai dishes carried a
   group with the *same* id `thai-protein`, but `Pa Kin Mao` prices its upgrades
   higher (beef +£0.30, king prawn +£1.10) than the other four (+£0.20, +£0.60).
   Anything resolving that group by id alone gave four of those dishes the wrong
   price table. Under the shared catalogue they are two distinct groups, and the
   export is keyed by (dish, group) so each dish keeps the prices it actually
   had.

3. **The `SMP-*` sample dishes left the shop menu.** Those nine "Sample:" items
   existed to exercise radio / checkbox / min-max / conditional / meal-deal
   behaviour. They are now `tests/fixtures/option-group-features.json` and will
   back the option-engine unit tests — the coverage survives, the fake dishes
   stop appearing on the counter.

**Credentials** (website print username / password) were stripped from the
committed bundle and put in `ringorder-epos-shops/demo/secrets.json`, which is
git-ignored.

**Archived** the checkpointed legacy database to
`ringorder-epos-shops/_archive/legacy-magicwok-epos-2026-08-14.sqlite`. The 20
development orders and 26 print jobs were not carried into the bundle — they are
test traffic, not configuration — but they exist in that archive if ever wanted.

The export script itself was a one-off against a schema that is being replaced,
so it is not kept in the repo; this entry and the bundle are the record.
