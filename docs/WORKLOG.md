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

---

## 2026-08-14 — Foundation, step 4: one price list

Price tiers came out of the schema, the bundle and the domain, a few hours after
going in. Web and marketplace orders arrive already priced by whoever took them;
the till prints and records them. A second price list here would be a copy
nobody maintains.

Tax classes stayed. A VAT band is about what the receipt has to declare, not
about charging a different price.

Migration 1 was edited rather than superseded, because no merchant has installed
it. From the first install onward it is append-only.

---

## 2026-08-14 — Interface, step 1: design tokens, the shell, and signing in

**Every colour and size now comes from `Styles/Tokens.axaml`.** Views carried
their own hex literals, which is why nothing quite matched anything else. Base
text is 16px (was 11–13), nothing a finger presses is under 44px, and anything
that takes money is 64. Colour means one thing only: green is cash, blue is
card, amber is held or owing, red is void or broken.

**The top bar became a cockpit** — shop, shift, who is signed in, the clock, and
status lights for the two things that fail silently: the printers and the web
feed. The printer light asks Windows whether each queue can actually be opened,
which catches the everyday failure of a renamed or unplugged printer before
someone reaches for a ticket rather than after.

**Signing in is now required.** The schema already demanded it — every order and
payment carries `staff_id` and `shift_id` — and a till that takes money without
knowing who is standing at it cannot answer the only question anyone asks when
the drawer is short. PIN pad, no keyboard, and the demo bundle seeds Manager /
`1234` with a change-me flag.

**Shifts open and close from that same bar.** Open with a float, close by
counting the drawer — and the count is entered *before* the expected figure is
shown, because a till that volunteers the answer first is not counting the
drawer, it is confirming it. Variance is recorded either way.

**Dishes are a fixed 5-column grid with paging, not a flow layout in a scroll
view.** This is the single biggest change to how the screen feels. Staff learn
where a dish is and stop reading; a flow layout moves every tile after a rename,
and a scroll position is wherever the last person left it. Page 2 of Chicken is
now always page 2 of Chicken. Sold-out dishes grey out and keep their place
rather than vanishing — a dish that disappears reads as "I am in the wrong
category", and staff go hunting.

**Keyed entry takes a quantity**: `3x88` adds three of dish 88 in one action.
Experienced staff work by number and barely look at the tiles.

Rail labels: the order screen is **Till**, which is what the trade calls it and
does not collide with **Orders** the way "Order" would.

Still to do on the interface: payment deserves its own full screen rather than a
keypad squeezed into the ticket column; Web orders should fold into Orders as a
channel filter now that the top bar carries the on/off.

---

## 2026-08-15 — Review: two authentication systems, and fixing it

A pass over the code found something I had introduced myself. The till had
**two** ways of proving who you are: real staff rows with salted PIN hashes and
roles, and `AppSettings.ManagerPin` — a single plaintext PIN shared by the whole
shop, still guarding void, drawer and 86. With a shared PIN, "who voided that
order" has no answer, which is the exact question the staff table was added to
answer.

**One system now.** `AppSettings.ManagerPin` and `CashierPin` are gone.
Everything gated asks for a `Permission`, and the check follows what the trade
actually does:

- Someone who already holds the permission is **not** challenged. A till that
  asks a manager to prove they are a manager teaches everyone to share a PIN,
  and the audit trail becomes fiction.
- Otherwise it is a **supervisor override**: the cashier keeps the screen and
  their half-built ticket, the supervisor types their own PIN, and the audit
  records both names.
- PINs are entered on a keypad, not a text box. A counter has no keyboard, and
  an on-screen QWERTY is a PIN read over the customer's shoulder.

`Permission.ReopenPaidOrder` was added rather than reusing Refund — reopening a
settled sale to add to it is its own act, and naming it that way means the audit
log reads like what happened.

**Settings → Staff became real people management**: add staff with a role, change
a PIN, switch someone off. Two guards come from real failures — a PIN already in
use is refused (two people sharing one cannot be told apart afterwards), and the
last manager cannot be switched off (nobody could open Settings again). Staff are
deactivated, never deleted, because their name is on every order they took.

**Discounts exist.** A shop asks for this on day one — "a fiver off", "ten
percent for the regular". One box takes either form: `5` or `10%`. A reason is
required, because a discount without one is an unexplained hole in the takings,
and both go to the audit log. The discount stays visible on the ticket with a
one-tap undo, so staff see it is still on before they take the money rather than
after.

**Migration 2 shipped** to carry the discount reason, which put the upgrade path
through its first real exercise: a database with 170 dishes in it was backed up,
migrated and reopened with everything intact. There are now tests for that
path — including one that writes a day's trading the way the *old* release wrote
it, because using today's repository to seed the "old" database would test
nothing.

55 tests.

Still to do: payment on its own screen; Web orders folded into Orders; Settings
sections for printers-as-devices, tax, receipt layout, backup and diagnostics.

---

## 2026-08-15 — Documentation, and tidying the shape of the code

Written for the case where the next session starts cold, which it will.

**The old documentation was deleted, not amended.** Seven of the eight files
described the product as it was before the rebuild: menu pulled from the
website, seed compiled into the binary, `%APPDATA%`, a shared manager PIN, a
schema built by `EnsureCreated`. A document that is confidently wrong costs more
than no document, because it is believed.

What replaced them:

| Doc | Settles |
|---|---|
| `AGENTS.md` | The rules, the layout, and a table of things that look like rules but are not |
| `docs/ARCHITECTURE.md` | The shape of the code and which decisions are load-bearing |
| `docs/SHOP_BUNDLE.md` | The bundle format, and the runbook for putting a shop live |
| `docs/INTERFACE.md` | Interface rules, the vocabulary on the buttons, and why each word |
| `docs/DEPLOYMENT.md` | Packaging, signing, update policy, backup, remote support, hardware plan |
| `docs/TESTING.md` | What runs automatically, and the manual passes that catch what it cannot |

`DEPLOYMENT.md` marks every section **decided**, **proposed** or **not built**,
because the packaging and signing choices are real spending decisions and a
future reader must not mistake a plan for a fact. The candidates — Velopack for
packaging, Azure Trusted Signing for the certificate — are written down as
candidates, with a note to re-check their terms.

**Code layout.** Four grab-bag files were split so a name says what is inside:
`EscPos.cs` gave up `TicketRenderer` and `RawPrinter`; `OrderRepository.cs` gave
up the print-job and customer repositories; `MenuRepository.cs` gave up settings;
and `PageViewModels.cs` — a name that describes nothing — became
`OrdersViewModel`, `OnlineViewModel` and `CustomersViewModel`.

**Sell became Till.** The rail has said "Till" since the interface work; the
code still said `SellViewModel`. Two names for one screen is how a codebase
starts lying to the next person reading it.

One thing worth recording as a method note: cleaning unused `using` directives
with a clever heuristic dropped ones that were needed, because the probe list
did not know about `Customer`, `PrintJob` or `AppSettings`. The compiler already
knows this answer exactly. Do not guess at something a tool can tell you.

---

## 2026-08-15 — Payment gets a screen, and Web orders stop being a module

**Payment is the whole screen now.** It was a 48px keypad in a 430px column,
which is the wrong place for the action with the highest cost of a mistake. What
is owed sits on the left in the largest type on the till; the keypad is on the
right with the note buttons (£5/£10/£20/£50) down the edge nearest the confirm
button, because the commonest case in a takeaway by a long way is a customer
handing over a note.

Both confirm buttons now name their amount — "Exact £10.60", "Card £10.60",
"Take cash £20.00". A button that says only "Card" makes the cashier verify the
figure somewhere else first, and the once they skip it is the one that goes
wrong.

The ticket is deliberately not shown on that screen: by then the dishes are
settled and the only questions are how much and by what.

The payment *logic* was not touched. Partial payment, split tender, change and
the settlement overlay had already been worked over and were behaving; this was
a change of presentation only.

**Web orders are no longer a module.** The top bar has carried the on/off since
the interface work, which left two places managing one thing. Orders now filters
by channel — all / counter / phone / web / platform — because that is how a shop
thinks about it: they are all today's work, and staff switching screens to find
out whether a website order landed are staff not serving anyone. The connection
test moved to Settings → Online, where it belongs: proving credentials is an
install-time job, while turning the feed off is a during-service decision and
stays in the top bar.

`OnlineViewModel` and `OnlineView` are deleted. That left `OnlineBadge` as dead
state — a dot for a nav item that no longer exists — and rather than delete it,
it became the thing it should have been: **a band across the top of the screen
when a web order arrives, which stays until someone opens Orders.** Nobody is
watching the screen at the moment an order lands, so a notification that fades
has not notified anyone.

55 tests, app boots.
