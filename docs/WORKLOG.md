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

---

## 2026-08-14 — Foundation, step 2: the product's own name

`MagicWok.Epos` became `RingOrder.Epos` across namespaces, projects and the
solution, and every default naming the case shop is now blank. Data moved from
per-user `%APPDATA%` to machine-wide `%PROGRAMDATA%\RingOrder\EPOS`, with
`profile/`, `backups/` and `logs/` beside the database — a second Windows
account must not open an empty till, and support needs one predictable path.

No legacy-adoption code was written. The only install is this machine, whose
data is archived and exported, and a dead upgrade path is a thing every future
reader has to understand for nothing.

---

## 2026-08-14 — Foundation, step 3: the schema everything else stands on

The pieces that are cheap to change now and expensive after the first merchant
install. All 35 tests pass, and the app boots, migrates and provisions itself
from the demo bundle.

**Versioned migrations** replace `EnsureCreated` plus ad-hoc `ALTER`s.
`schema_migrations` records what ran; the runner takes a `VACUUM INTO` backup
before touching an existing database. Migrations are append-only and there is no
"down" — a bad release is fixed forward or restored from that backup.

**Money is INTEGER pence in SQLite.** `decimal` stays in the domain because .NET
decimal is exact; the defect was `REAL` columns. Conversion rounds half away
from zero, so a till never rounds a half-penny to even and lands the day out.

**Lines and payments are tables**, not JSON columns on the order. "What sold
this week" and "what did each till take" cannot be answered from a blob.

**Staff, shifts, cash movements and an audit log exist now**, and every order and
payment carries `staff_id`, `shift_id`, `channel`, `price_tier_id` and
`terminal_id`. The features come later; the columns had to come first, because
back-filling them means a data migration on every shop rather than a schema
change on none. PINs are PBKDF2 hashes with per-user salt, never stored in the
clear. Shift totals are summed from the rows that carry the shift id rather than
accumulated into a running column, so a crash mid-service cannot leave a total
that disagrees with its own payments.

**Service type and channel are separate.** `Collection | Delivery | EatIn` is
how the customer gets the food; `Counter | Phone | Web | Platform` is where the
order came from. The old single list conflated them, which cannot express a
phone order for delivery or a marketplace collection. "Waiting" turned out not
to be a fourth type at all — it is a collection order with the customer standing
there, so it is a flag that prints WAITING on the ticket.

**Option groups are a shared catalogue** with per-dish links carrying sort order
and the conditional reveal. Editing "spice level" is now one edit rather than
fifty, and the dish editor reports which other dishes a shared edit touches.

**Provisioning replaced seeding.** `BundleImporter` reads a shop bundle from the
profile folder on first run; the embedded Magic Wok menu is gone from the
binary. A till with no bundle says it needs setting up rather than serving
someone else's menu. Re-import replaces the catalogue and leaves orders,
customers and shifts alone — a menu update mid-week must not erase the week.

**Tests exist** — 35 of them, over money conversion, the option engine (using the
old SMP-* fixtures), bundle-import fidelity against the demo shop, tender and
shift arithmetic, and permissions. `tools/SmokeSeed` is deleted; the test
project does its job properly.
