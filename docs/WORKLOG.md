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

---

## 2026-08-15 — Printing becomes an architecture

Four layers, so that a shop with four printers of three kinds never has any of
that reach the sale.

**Transports**: Windows queue, raw TCP 9100, serial (which is how a paired
Bluetooth printer appears on Windows), and file for support. TCP printers are
also asked for paper and cover status, which the spooler cannot report.

**A device registry** replaces two printer names in settings — transport,
address, paper width, encoding, and which one the drawer hangs off.

**Routing rules** match document, print class, service type and channel to a
device with copies and a fallback. Every matching rule fires, because a dish
printing at the wok and again on the packing bench is a shop asking for two
copies, not a conflict. `PrintRouting` is pure and has nine tests covering the
shapes shops actually ask for.

**A real queue**: jobs are rows carrying their rendered bytes, one worker per
device, retry with backoff, five attempts then held for deliberate reprint, and
anything left mid-print by a crash is requeued at startup. Queueing cannot fail
for want of paper — a kitchen printer switched off at 6pm must not stop the till
taking money at 6:01.

A nuance recorded because it reads as a contradiction of "paper is the truth": a
ticket is marked sent when **queued**, not when paper appears. Waiting for paper
would let two people send the same lines twice while the first job retries, and
the queue is durable. The rule still governs the job's own status, which is what
the reprint list and the printer light read.

**Settings, Hardware is now a printer list**: add, name, choose the transport,
type the address with a hint that changes per transport, mark which one has the
drawer, test it, remove it. Testing reaches the device, asks its status where it
can, then puts paper through it — all three, because a printer can answer on the
network with its cover open.

### A defect this uncovered

Background print workers immediately produced `cannot start a transaction within
a transaction`. `EposDb` cached **one** SQLite connection for the whole
application, which was invisible while a single thread touched the database and
wrong the moment anything ran in the background.

Fixed properly rather than papered over: `Open()` hands out a pooled connection
per call and the caller disposes it, with WAL for concurrent readers and
`busy_timeout` so a second writer waits its turn instead of failing. This is the
standard pattern for SQLite in a multi-threaded process, and the previous design
would have failed on a merchant's machine the first time a printer was slow.

The dev database was rebuilt from the bundle rather than adapted in code. No
merchant has an install, and a settings-to-devices upgrade path would have been
dead code for every shop that will ever exist.

64 tests.

---

## 2026-08-15 — Logging, backups, support — and two defects they exposed

Everything a shop we cannot visit needs us to be able to see.

**A dated log file** under `logs/`, thirty days kept. Startups, migrations,
provisioning reports, print failures and crashes. Unhandled exceptions and
unobserved task faults are caught in `Main` and written down — a till that dies
without a word leaves a shop not knowing whether the last order was saved.

**Nightly backups**: `daily-<date>.sqlite` on the first startup of each day,
re-checked hourly, fourteen days kept. The hourly re-check is not belt and
braces — a shop that closes before midnight is never caught by a 3am schedule,
and one that never turns the till off is never caught by a startup-only one.
Three tests, including a backup taken while orders are being written.

**Settings → Support**: version, machine, data folder, schema version, shop,
printer health, queue depth, web-order status, last backup, log folder. Plus
**Export diagnostics** — one file with all of it and the recent log, to send us —
and **Back up now**. And a **reprint list** for tickets a printer gave up on,
with the error against each; deliberate, never automatic, because a queue that
retries forever is how a kitchen ends up with forty copies of one order.

### Defect one: two instances could move a shop's database

`LocalPaths` probed whether `%PROGRAMDATA%` was writable using a **fixed**
filename. Two tills starting together — someone double-clicking the icon twice —
could delete or lock each other's probe, and the loser concluded ProgramData was
unusable and silently moved the shop's database into its own user profile.

Found because the log stopped appearing where it should. On a merchant's machine
this is "the till lost all of today's orders" with no explanation available.

Three fixes: the probe is named per process; falling back now records a reason
and logs a loud warning, because a till quietly keeping its data somewhere else
is worse than one that complains; and **the app refuses to start twice** on one
PC. Two tills sharing a database would double-print and disagree about the shift.

### Defect two: the log buffered itself into silence

The first version queued lines onto a background writer. It wrote the startup
line and nothing after — the log looked healthy while everything of interest
vanished, which is the worst possible failure for a log.

Rewritten to append synchronously under a lock. A till writes a few hundred
lines an hour; buffering that bought nothing and hid a fault. Write failures now
set `LastWriteError`, which diagnostics prints, so a log that cannot write says
so instead of being trusted.

Both were mine, both were invisible until something else went looking, and both
would have shown up first on a merchant's PC. Worth stating as a rule: **a
component that can fail silently must report its own health somewhere a human
looks.**

67 tests.

---

## 2026-08-15 — Print routing becomes something a shop can configure

The routing model existed but could not be reached: rules matched on a dish's
station, and nothing in the interface set one. A shop adding a fryer printer
still needed us to edit their bundle, which is the opposite of the whole design.

**Stations are set on the category** — "Starters cook at the fryer" — with a
per-dish override for the exceptions. That is how a shop thinks about it: in
sections, not in forty individual dishes.

**A route editor in Settings**: document, station, service type, channel, target
printer, copies, and a fallback, with "(any)" first in every filter because most
rules are broad. Removing the last kitchen rule asks for confirmation, since
tickets would then fall to whichever printer happens to be first.

### The half-wire this exposed

A dish that follows its category has a null station. The order line took that
null, and `PrintRoute.Matches` compares a rule's station to the line's — so
**every dish that inherited would have matched no rule and reached no printer.**
The demo shop's bundle sets a station on every dish, so nothing looked wrong.

Fixed by resolving on load: the repository copies each category's station and
tax band onto its dishes, `EffectivePrintClass` falls back category → kitchen,
and the till writes the resolved value onto the line. Three tests now cover
inherit, override, and nothing-set-anywhere.

Worth noting the shape of this mistake, because it is the second of its kind:
**a feature configured in one place and consumed in another is not finished
until something exercises the path between them.** Both times the missing link
was invisible in the demo data.

70 tests.

---

## 2026-08-15 — VAT, from the setting through to the receipt

Tax classes had been threaded from the bundle to the order line since the
schema rebuild, and nothing had ever computed a penny of tax. Closed end to end
rather than left as another field nobody consumes.

**The arithmetic runs backwards.** UK retail prices include VAT: £6.00 at 20% is
£5.00 net and £1.00 VAT, not £6.00 plus £1.20. The other direction overstates a
shop's takings by a fifth, which is the sort of error an accountant finds a year
later.

**A discount is apportioned across the bands** in proportion to line value
before VAT is worked out — £2 off a half-hot, half-cold ticket takes £1 off each
rather than all of it off the standard-rated half. Delivery follows the shop's
default band, being ancillary to the food.

**Nothing prints unless the shop is registered.** The VAT number being blank
means the shop is below the threshold, which most small takeaways are. A receipt
claiming VAT from a business that cannot charge it is worse than one that says
nothing, so the whole block is silent until a number is entered.

**Settings → VAT**: the number, whether prices include tax, the rates by band,
and the receipt footer. The hint text changes with the state, so an unregistered
shop is told that showing no VAT is correct rather than left wondering.

Thirteen tests, including a property test that net plus VAT reconstructs the
gross for every penny from 1p to £50 — the one thing a customer can check by
looking at the paper.

83 tests.

---

## 2026-08-15 — Postcode lookup, and the honest answer about "free"

Asked whether the industry-standard "type a postcode, get the address" belongs
in the till, and whether a reliable free service exists to plug in.

**It belongs, and there is no free option that does the job.** Every service
that can turn a postcode into house numbers resells the Royal Mail Postcode
Address File, and the Royal Mail charges for it. The genuinely free one —
postcodes.io, Ordnance Survey open data, no key, self-hostable — confirms a
postcode exists and names the district, but has never heard of number 12.
Checked rather than assumed: Ideal Postcodes is pay-as-you-go at roughly 3–4.5p
a lookup from prepaid credits with a 50-credit trial; getAddress.io has a free
tier for low volume and paid plans above it.

So the shop chooses between four options and **off is the default**. A till that
quietly starts spending a merchant's credits is not a till they trust.

**The cache is the commercial argument.** A takeaway delivers inside a few miles
and serves the same streets for years — a couple of thousand postcodes that never
change. Every answer is stored forever, so a paid lookup is charged once per
postcode for the life of the shop rather than once per phone call. That turns
"do we need a subscription" into "no". Settings shows postcodes saved and times
reused, because that number is what a merchant wants when a bill arrives.

Which makes **normalising load-bearing, not tidiness**: `b296aa`, `B29 6AA` and
`B29  6aa` are one house, and if they are three cache keys the shop pays three
times. `UkPostcode` packs, uppercases, and re-inserts the single space before the
last three characters; everything downstream keys on that. Customer addresses are
now saved normalised too, so the history search finds them later.

**Three sources, cheapest first**: the cache, then the provider, then the shop's
own delivery history. History last, deliberately — it knows only the addresses
this shop has already delivered to, so offering it ahead of a real answer would
suggest number 12 to a new caller at number 40. For a shop that never turns a
provider on, history is the whole feature, and it costs nothing.

**Only real answers are cached.** "No such postcode" is permanent and worth
keeping. A timeout says nothing about the postcode, and caching it would make one
bad minute permanent.

**Nothing blocks the address fields.** Four-second timeout, every failure lands
on plain wording ("credits have run out", not "402"), and staff can ignore all of
it and keep typing. A lookup that stops an order being taken has cost a sale to
save keystrokes.

On the till the delivery fields are now **postcode → Find → street**, the order a
UK delivery is actually established over the phone. A single result fills itself
in rather than asking someone to pick from a list of one. The same panel serves
the phone book, because two copies would drift the first time one was fixed.

The API key lives in the till's database and in `secrets.json` at provisioning,
never in the shop bundle — bundles get emailed and copied, and a leaked key
spends someone else's money. It is kept out of the audit trail for the same
reason.

**Two defects found while building it.** `default(UkPostcode)` threw on every
property because a struct's auto-properties skip the constructor and left the
strings null — caught by the empty-string case in the validation table, fixed
with backing fields. And the results list was first written with a
parent-relative command binding, which XAML compilation cannot check; it became a
`ListBox` bound to `SelectedCandidate`, which needs no parent lookup and puts the
pick logic somewhere a re-entrancy guard can protect it.

Migration 4 ran on the live shop database with the usual pre-migration backup.
Verified afterwards: 21 categories, 170 dishes, 11 shared option groups, 14
per-dish links, 37 option choices — unchanged.

118 tests.

**Not done, and needs saying:** the results list is covered by unit tests and the
markup compiles, but nobody has yet clicked a candidate on a running till, and no
real provider key has been exercised against a live API. Both are worth ten
minutes during the printer session.

---

## 2026-08-15 — Places apart from people, and what erasure means

Two things landed together because they turned out to be the same design.

**The Ideal Postcodes key arrived**, and the chain was proven against the live
API for the first time: `B44 0QN` returns 24 real doors, `BIRMINGHAM` comes back
cased down to `Birmingham`, coordinates and the postcode land on every candidate.
That closes the "nobody has exercised a real key" note from the last entry.

First, though, `.gitignore` had `.env` — which does **not** match `.env.local.txt`.
The key was never committed, but it was one `git add -A` away. The patterns are
now deliberately wide: `.env.*`, `*.env`, `*secrets.json`, `*.key`, `api-key*`.

**Address is now separate from CustomerAddress.** `addresses` holds a door;
`customer_addresses` holds one person's link to it, with the label and the note
for the driver. That buys four things at once: a door is stored once however many
customers live behind it, one customer can hold several addresses, a place typed
by hand gains coordinates the first time a lookup covers it, and — the reason it
matters most — the personal data is now isolated in the link.

Which is the GDPR answer, not a separate feature. A street with nobody attached
is geography. **Erasing a customer removes the links and leaves the map.**

**Erasure keeps the sale and drops the identity.** HMRC wants six years of records
behind a VAT return; GDPR gives a right to be forgotten; they are reconciled by
scrubbing name, phone, address, `customer_id` and — most easily forgotten —
`online_payload`, the raw marketplace JSON that holds the customer's details long
after the columns beside it were tidied. Totals, VAT and service type stay.
Orders taken before the caller was saved to the phone book are matched on the
normalised phone number, because an erasure that left those behind is not one.

**Retention ships as 0: nothing is removed automatically.** The merchant is the
data controller, and a till that deleted their phone book on first upgrade would
be indefensible. Settings → Customer data states the obligation, shows how many
records are past whatever period they try, and leaves the button unpressed. A
second switch, also off, lets it run at startup once they have seen the number.
Both erasure paths log counts only — an audit line repeating the names would
reinstate exactly what it recorded the removal of.

New: [docs/PRIVACY.md](PRIVACY.md), written for the merchant question "what does
it actually do with my customers' data", including what erasure does *not* touch
(free-text order notes, and backups until they age out).

Migration 5 creates the tables; `AddressBackfill` moves the old
`customers.addresses_json` across in C#, not SQL, because the fingerprint that
decides whether two rows are the same door must be computed by one piece of code.
Each customer moves in its own transaction and its blob is emptied as it goes, so
the pass is resumable and re-running it does nothing.

**A defect found while looking at the backup folder.** `BackupBeforeMigration`
wrote to the machine-wide shop directory whatever database was being migrated, so
every test run since the project began had been dropping a five-row database into
the live shop's backups — 23 of them had accumulated. The restore instruction is
"take the newest pre-migration file", and the newest was usually a test. Backups
now go beside the database they came from; the strays are deleted; a test asserts
it. This was the more dangerous bug of the day: a safety feature that could have
handed someone an empty database during a real recovery.

Also closed: an address chosen from a lookup was being filed as `Manual` with no
coordinates, throwing away exactly what the delivery-zone work will need. It is
now stored as `Lookup` with its latitude and longitude — unless the text was
edited afterwards, in which case the staff member overrode the provider and their
words are not filed under the provider's coordinates.

Migration 5 ran on the live shop database with its pre-migration backup.
Verified after: 170 dishes, 21 categories, 11 shared option groups, 14 per-dish
links, 37 option choices — unchanged.

146 tests.

**Still not clicked on a running till:** the results list, the erase-customer
button and the Customer data screen are covered by tests and compile, but the
interaction itself is unexercised. Worth ten minutes alongside the printer.

---

## 2026-08-15 — Refunds: give money back without rewriting the sale

Voiding was the only lever the till had, and it does not return money. Closed
the gap that a real shop hits every week.

**A refund is a new record, never an edit.** The order keeps its lines, totals
and VAT exactly as rung up, and the refund sits beside it. The shop has to be
able to show both halves — what was sold and what came back — and a till that
quietly reduced yesterday's takings could explain neither.

**A void and a refund are different events.** A void says the sale never
happened; a refund says it did and was reversed. The void prompt used to warn
"refund customer manually if needed", which was the honest thing to say when
there was no refund. It now says to use Refund instead.

Each refund writes two rows in one transaction: the reason, staff and shift in
`refunds`, and the money in `payments` — **negative, and flagged**. The negative
amount means every sum already written over `payments` keeps working and becomes
a net figure without knowing refunds exist, and the shift's expected cash comes
out right for free: money handed over the counter is money out of the drawer.

**Two traps that came with that choice**, both found by thinking about the
existing code rather than by a failing test:

- A refund loaded as a negative tender would pull `AmountPaid` down and push
  `BalanceDue` back up, so a refunded order would look like it owed money and
  could be settled a second time. `PosOrder.Refunds` is a separate list.
- Saving an order rewrites its rows in `payments`. That wipe would have deleted
  refunds on the next ordinary save — after printing, say. It is now scoped to
  `is_refund = 0`.

A third was already in the shift query: it summed *all* payments to decide
whether an order was settled, so a fully refunded sale would have dropped back
among the unpaid and invented money the shop was owed. Gross now, refunds
excluded — it was a paid sale, and the refund shows on its own line.

**The report shows both numbers.** Gross taken, refunds out, net kept. "Took
£1,200 and refunded £45" is a different conversation from "took £1,155", and
only one of them tells an owner to go and look.

**Rules that refuse a refund** live in a pure `RefundPolicy`: never more than was
actually taken less what has already gone back (the order total is not the
ceiling — a part-paid order can only return what it received), never twice for
the same line, never without a reason, never on a voided or unpaid order. The
suggested tender is whichever one most of the money arrived on, because cash back
on a card sale is the shape of most till fraud and should be a deliberate change
rather than the default. Full-refund-by-lines carries a penny of tolerance so
three lines at £6.67 against £20.00 taken are not refused by a rounding crumb.

**VAT reverses at the rate the sale was made at** — exactly per line when the
refund names them, scaled by the fraction returned when it is a bare amount.

The refund slip is its own document, not a receipt with a minus sign: what went
back, why, how it was returned, and what is left on the sale.

On screen: tick the dishes that were wrong or type an amount, pick the tender,
give the reason, confirm. Gated on `Permission.Refund`, which existed in the enum
and had never been used by anything.

Migration 6 ran on the live shop database with its backup. 167 tests.

**Still unclicked on a running till**, now including the refund panel. Worth
doing in one pass with the printer, since the refund slip wants paper anyway.

---

## 2026-08-15 — Delivery by postcode, and a surcharge that was taxed but never charged

Every delivery was charged one flat fee. Zones close that, and closed two
half-wired things on the way.

**Priced by prefix, not distance.** That is how a takeaway publishes its area —
"B44, B23, B42 — £2", not "£1 per mile". Staff can check a prefix without a map,
it works with the broadband down, and it avoids the argument that begins "your
screen says 3.1 miles but I'm 2.9". Distance banding was considered and left
out: it needs a coordinate for every new address, and the coordinates we have
are straight-line, which is not what a driver drives.

**Longest matching prefix wins.** `B440` beats `B44` beats `B4`. The broad entry
catching a district the shop never listed is deliberate, not a bug — a shop that
writes `B4` with no `B44` means that side of town, and charging the wider zone
beats declaring a real customer unreachable. The till names the zone it matched
so a shop can see it happen and narrow it if that was not the intent.

**A minimum never blocks the order.** It is measured on the food — after
discount, before the fee, so the fee cannot help justify itself — and being
under it warns. Whoever is on the phone decides; a shop can switch on a surcharge
that tops the order up instead. A till that refuses outright is a till staff work
around, and that loses the record along with the sale.

A shop with no zones behaves exactly as before: one default fee, nothing said.
Worth stating, because "no zones configured" and "outside the delivery area" are
easy to conflate and only one of them should ever stop anyone.

**Two half-wired things closed.** The shop bundle has carried a `zones` list
since the schema rebuild that nothing imported — a merchant could describe their
whole delivery area in the bundle and be charged the flat default anyway. And
`BelowMinimumSurcharge` was **included in the VAT calculation but left out of the
order total**: a web order carrying one had tax worked out on money the customer
was never charged, which is the shop declaring VAT on takings it did not take.
There is now a test asserting the gross the VAT was computed on equals the total
the customer pays — the invariant that should have been there from the start.

Migration 7 ran on the live shop database with its backup. 188 tests.

**Unclicked on a running till**, as with the last three rounds: the zone editor,
the test-a-postcode box, and the amber note on the delivery panel. All covered by
tests and compiling. Worth one pass with the printer.

---

## 2026-08-15 - Delivery realigned with the website, and a matching rule corrected

Read the RingOrder website's delivery engine (`ringordersite`,
`src/lib/delivery/`) and found the zones shipped a few hours earlier were wrong
in a way the site had already solved and documented.

**Matching was string prefixes. It is now postcode components.** The site's own
comment states the rule: B47 must never match a B44 rule, and with only "B44 0"
and "B44 1" rules a "B44 3" postcode matches neither. My version would have
priced a B47 delivery at the B4 rate - different districts on opposite sides of
the city - and I had written a paragraph rationalising it as a deliberate
fallback. It was not; it was wrong. Rules now parse to one of four levels (area,
district, sector, unit) and match exactly at that level, most specific winning.

The space turned out to be load-bearing: "B44 0" is a sector and "B40" is a
district. My previous normalisation squashed spaces out, collapsing one into the
other.

**The below-minimum surcharge was the shortfall. It is now a flat amount**, which
is what the website charges and what already arrives on a web order. The old
behaviour meant the same shop quoted two different numbers for one basket
depending on which product the customer used.

**Free-over of zero now means "no threshold"**, matching the site's reasoning:
"free from the first penny" is a delivery fee of zero, and giving one idea two
spellings is how a merchant clears the box and makes every order free.

Thresholds and minimums compare against the basket **before** discounts, again
matching - a voucher must not quietly withdraw free delivery already shown.

**Distance pricing added, with the site's own stack**: postcodes.io for
coordinates, public OSRM for road miles. Last round I said our coordinates were
straight-line and therefore not worth pricing on; that was wrong - road distance
is available free, and the website already uses it. Routing runs once per
postcode in the background and is cached forever, so the till keeps pricing when
the public router is down.

The lesson worth keeping: **the two products are one price list.** Delivery rules
are not ours to design independently. `AGENTS.md` now says so.

Migration 8 ran on the live shop database with its backup. 197 tests.

---

## 2026-08-16 — X and Z readings, and what a shift report is allowed to say

`ShiftRepository` had carried a comment saying "this is what an X or Z report
prints" since the schema rebuild, and nothing printed one. `PrintDocument.Report`
was in the enum with nothing producing it. Closed end to end — the third time
this shape of gap has turned up, and the second where the enum member was the
only evidence it existed.

**No migration, and no snapshot table.** A shift's totals are summed from the
rows carrying its id, which is the rule the rest of the till already follows. A
stored copy could drift from the rows it claims to summarise, and then neither
number is worth anything.

**The reading is pure.** `ShiftReportBuilder` takes the shift, its totals, its
orders and the tax classes, and returns a `ShiftReport`. Like `TaxCalculator`
and `RefundPolicy` it does no I/O, so the arithmetic on the one piece of paper an
owner checks every night is tested without a database or a printer.

**VAT is summed per order, never recomputed on the day's gross.** This is worth
a penny-level test and has one. VAT rounds on each sale because that is what the
customer was charged and what their receipt says; working it out again on the
shift total rounds once instead of two hundred times, and the report would then
disagree with the pile of receipts behind it. That discrepancy is exactly what an
accountant asks about a year later.

**A void is counted and then kept out of everything else.** A void says the sale
never happened, so leaving it in the service-type split would inflate the first
figure an owner reads.

**Two things the report must not claim.** Both were found by reading the code
rather than by a failing test, and both had already been written down wrongly in
my first draft:

- A payment is written against the **order's** shift, not the shift that was
  open when the money was taken (`OrderRepository` binds `order.ShiftId`). That
  is deliberate — it is what stops a reopened ticket moving yesterday's money
  into today. The consequence is that **settling an old unpaid ticket adds to
  the shift it was rung up in**, so a Z reprinted later can differ from the one
  that came out at close. The counted cash, the expected figure and the variance
  are frozen on the shift row and never move. There is a test pinning this down,
  because it is surprising and someone will otherwise call it a bug.
- Takings and settled-sales value differ whenever money is sitting on an open
  ticket. My first version explained that difference as orders crossing shifts,
  which this schema does not do. The report now names the real cause and only
  when there is actually an open ticket.

**Settings, Shift was a sum over "today" and is now a reading per shift.** Not a
cosmetic change: a shop trading past midnight had every figure silently split
across two dates, and one that opened twice in a day had both sessions added
together. The drawer is counted per shift, so the reading has to be per shift.
Closed shifts are listed with their variance, and any Z can be viewed or
reprinted.

**The Z prints itself at close**, queued and never awaited — a printer out of
paper must not leave a shift half-closed, and the reading can be reprinted.

`PrintRouting.RouteStandalone` is new: a report belongs to no order, so it has no
service type or channel to match on. A rule that narrows on those was written
about tickets, and letting it apply here would silently swallow the one document
an owner prints by hand. Two tests cover that.

The screen and the paper share `ShiftReport` but not their layout — ESC/POS needs
sizes and bold that mean nothing in a text box. What must not fork is the
arithmetic, and it cannot: both render the same record.

`ShiftTotals` moved from `RingOrder.Epos.Data` into the Domain project. It is a
pure value type describing a shift and it was the only thing stopping the report
from living in Domain.

### A flake this uncovered, and its cause

Adding a sixteenth test class made `BundleImportTests` fail about one run in ten
while passing on its own — the worst shape of flake, because the test that fails
is not the one at fault.

Nearly every test class tears down with `SqliteConnection.ClearAllPools()`, which
is **process-wide**, and xunit runs test classes in parallel. One class's
teardown was pulling pooled connections out from under another class mid-test.
Parallelisation is now off for the assembly: the suite runs in about three
seconds, so it costs nothing worth measuring. The deeper fix is for each class to
clear only its own pool — which `EposDb.Dispose` already does correctly — and the
`ClearAllPools` calls beside it can go whenever someone is in there.

213 tests, six consecutive clean runs.

**Not clicked on a running till**, consistent with the last four rounds: the
readings screen, the reprint buttons and the automatic Z at close are covered by
tests and the XAML compiles, but nobody has pressed them. Now being addressed
properly rather than noted again — see the next entry.

---

## 2026-08-16 — Caller ID and the card terminal, designed before the hardware arrives

Both had been interfaces with a fake behind them since the beginning:
`SimulatedCallerId` and a `ManualCardTerminal` that returned success
unconditionally. The hardware is not here yet, so the protocols are built to the
industry shapes now and the devices plug into them later.

### Caller ID

**There is no single format.** BT lines here deliver MDMF over several lines;
cheap USB boxes emit SDMF all on one; a few firmwares invent their own labels.
`CallerIdDecoder` is pure and line-at-a-time, so each of those is a test rather
than a shop reporting that the popup stopped working.

**`P` is not a phone number.** A withheld call sends `NMBR = P` and an
unavailable one `NMBR = O`. Stored as a number that puts a customer called "P"
in the phone book and searches for them on every withheld call afterwards.
Withheld and unavailable are also told apart, because one is the caller's choice
and the other is the network failing to help, and they deserve different wording
on screen.

**One call, one popup.** A phone rings six times and some boxes repeat the
number on every ring. Deduplicated on the number within twenty seconds.

**It reconnects on its own.** A till runs for weeks and someone unplugs the
phone box to hoover; a caller display that stays dead until the next restart is
one the shop stops trusting. Failures are recorded rather than thrown, because a
till must open whether or not the box is plugged in.

A bug caught by its own test: the first version emitted the call as soon as the
number was known, and MDMF sends `NAME` *after* `NMBR` — so the caller's name
was thrown away every time the network supplied one. A call now ends only at a
boundary (blank line, next RING, or the read timeout).

### The card terminal

Rebuilt around the one failure that matters: **charging a customer twice.**

**The till assigns the reference, not the terminal.** That is what makes a lost
answer recoverable — a till that waited for the terminal to name the transaction
has nothing to ask about afterwards. The reference is the tender's id.

**`Unknown` is not a decline.** It is its own outcome, and it is the reason
`PaymentResult` carries an enum rather than a bool. The cable gets pulled, the
terminal reboots, the answer never arrives. A till that reads that as "declined"
tells the cashier to take the money again from a customer who has already paid.

**A sale is never retried. It is queried.** `QueryAsync(reference)` asks the
terminal what became of it. Approved means record it; a reference the terminal
has never heard of means nobody was charged and is the only case where the till
may safely conclude the money did not move; still unknown means the till does
neither and says so on a dialog naming the reference.

`ManualCardTerminal` stays, and is not a placeholder — most small takeaways run
a standalone machine and tell the till what happened. What it cannot do is
check, so its `QueryAsync` returns `Unknown` rather than inventing an approval.
A till asserting something it cannot know about a customer's money is the thing
being designed against.

`SimulatedPaymentTerminal` behaves like real hardware with none attached, and
can be told to lose an answer — the case that is impossible to stage on a real
terminal on demand and is exactly the one that has to work.

`IHardware.cs` gave up both to `CallerId.cs` and `PaymentTerminal.cs`. Its name
described nothing, which is the same tidy-up `EscPos.cs` had.

231 tests.

---

## 2026-08-17 — The simulator was lying, and a restore that can be undone

### The simulated terminal returned too fast to be honest

Reported from a real run: arming "next card loses its answer" produced an
ordinary paid sale with no sign of the recovery. The recovery *had* happened and
the result was right — but `Task.FromResult` returns synchronously, so the
till's "no answer — checking" line was set and overwritten inside one
continuation and the UI thread never yielded to paint it. A lost answer looked
identical to an ordinary sale.

Two fixes, and the second matters more:

- **The simulator now takes time**, because real hardware does. A simulator that
  returns instantly does not simulate the thing it exists to test.
- **A recovered payment is written down**: an audit entry, a log line, and
  "(recovered)" on the tender's reference. A status line is not evidence. This
  till already has the rule — *a component that can fail silently must report
  its own health somewhere a human looks* — and "we had to ask the terminal what
  it had done with a customer's money" is exactly what someone reconstructs
  weeks later, by which time the screen has moved on.

### Restore

`DEPLOYMENT.md` has said "restoring means stopping the till and copying a file"
since the backup work. Closed.

**The swap happens at the next start**, never while running. WAL means two more
files beside the database and a pool of live connections; copying over that from
inside the process yields a database that is neither the backup nor the
original. So a restore is a marker file, honoured before anything opens the
database and deleted first — a marker naming a file someone has since deleted
must not make the till fail the same way at every start, which is a shop that
cannot open.

The replaced database's `-wal` and `-shm` are deleted. Left behind, SQLite
replays them over the restored file and undoes part of the restore, which is
worse than failing outright.

**The live database is kept first.** A restore has to be undoable; "I restored
the wrong day" must not be the end of a shop's records. **And the confirmation
counts the damage** — "43 orders worth £912.60 will be gone" — because a prompt
that names no consequence is one people learn to click through.

`RestoreRequest` takes its paths rather than reading `LocalPaths`. Not caution
for its own sake: `BackupBeforeMigration` once wrote to a fixed machine-wide
path and every test run for months dropped a five-row database into the trading
shop's backups. A restore reaching the wrong folder is that same bug with the
destruction already done. Verified after this round that the live backup folder
holds only real backups and no marker.

238 tests.

---

## 2026-08-17 — Licensing decided, and support scripts that found a real fault

### Licensing

**An offline signed licence file. Decided, not built** — see
[DEPLOYMENT.md](DEPLOYMENT.md).

The cloud services were priced (Keygen: free dev tier, self-hostable CE;
Cryptolens from ~€199; LicenseSpring from ~$299/mo) and rejected on
**architecture, not cost**. All three assume the client reports to a server, and
this till's first rule is that it sells food with the router unplugged. A licence
check needing the internet is the remote dependency the product already refused.

ECDSA P-256 in `System.Security.Cryptography`: no package, no service, no monthly
cost, about 150 lines. It carries the merchant, the type, the dates and the
**edition** — which is the right home for it, because `edition` in the shop
bundle is a field a merchant can edit and in a signed licence it is not.

Three decisions taken deliberately: it **expires but only nags**, never stopping
a shop trading; it is **not on the receipt**, only in Settings and diagnostics;
and it is **not bound to the machine**, because a hardware fingerprint breaks
when a merchant replaces a dying PC and defends against copying that is not a
real risk here. The opt-in heartbeat would reveal a shop running two tills
anyway — **detect, do not enforce**, which costs nobody a service when it is
wrong.

**And then a steer that settles how much of this is worth building at all**,
recorded because the instinct to over-build here is strong and the next session
will otherwise re-derive it differently: an EPOS is a mature product, nothing in
this codebase is a secret, and a competitor gains nothing from a copy. **The
till is phase one.** The parts worth protecting go to the cloud — as web orders
already have — where access is controlled by the service rather than by a file
on a shop's PC, and the till receives and prints. Licensing here is a label, not
a lock. If it ever grows past a signature check and a line in Settings, it has
become the thing the deployment doc says not to build.

### Support scripts

`tools/support/ringorder-support.ps1`. Everything in it is also in Settings →
Support; it exists for when that screen cannot be reached, which is when it is
needed most.

`Restore` from the script only writes the marker — the swap is always done by
the till at startup. A script cannot know whether the till is holding the file,
and this is not the place to find out.

**Two bugs in it, both found by running it rather than reading it:**

- `Collect` piped the report functions into `Out-String`. They report through
  `Write-Host`, which goes to the console and **not** to the pipeline, so the
  file that reached us would have been almost empty — a diagnostics tool that
  silently collects nothing being about the worst shape of this particular
  feature. Rewritten around `Start-Transcript`.
- `Format-Table` streams lazily and its output landed after whatever printed
  next, so the sections in the collected file came out interleaved. Forced with
  `Out-String`.

### What it found on the development machine

`Printers` reported `GLPrinter80` **offline with 29 jobs stacked in the Windows
spooler** — every ticket printed during this week's testing had queued and never
come out. The app was right at every step: it queued the jobs and its own status
light reads the queue. But nothing had told anyone that Windows was holding
them, which is precisely the silent, expensive failure the script was written
for. It found one on its first run, on our own machine, before any merchant had
it.

---

## 2026-08-17 — The delivery board, and the money that is not in the drawer

`INTERFACE.md` had listed "a Delivery screen for driver dispatch" since the
interface work. Built, and it turned out to close a hole in the shift reading
made a day earlier.

**The gap.** Cash that goes out with a driver is neither in the drawer nor a
completed sale. A shift that looks £60 short at eleven o'clock is usually a
driver who has not come back yet, and a till that cannot say so sends someone
looking for a thief. The Z reading now states it **below** the expected figure
and never inside it — that money is genuinely not in the till, and folding it in
would make an honest count look wrong.

**Both kinds of merchant run the same binary.** Some shops employ drivers; some
deliver entirely through Uber Eats and Deliveroo. The board appears only when
someone is graded a driver — derived from the staff list rather than from a
setting, so a merchant who hires a driver gets the screen by adding them, and one
who never will never sees a screen about drivers. Nothing configured behaves
exactly as before.

**A prepaid delivery puts no cash in a driver's pocket.** Web and marketplace
orders were paid at checkout, so the driver carries food and not money. Counting
the order total would have shown every driver owing the shop the price of their
whole round. `CashToCollect` is the unpaid balance, never the total.

**Drivers are staff.** They carry the shop's cash and anything that takes money
and cannot name the person who took it is unfinished. `StaffRole.Driver` grants
**nothing** at the till, though — falling through to the cashier default would
have handed everyone with a van the till, which the permission switch now says
explicitly rather than by omission.

**Settling names two people.** The tender is stamped with whoever is signed in
(who received the money) while the order keeps the driver who collected it.
Those are usually different people, and "the drawer is short" needs to tell them
apart.

**A run is not a table.** Three nullable columns on `orders` — driver, out, back
— and "what is Wei carrying" is derived from them, the same way shift totals are
summed rather than accumulated. Nothing can drift from the rows behind it.

**Concerns warn and never block.** Sending an order the kitchen has not printed,
or one with no address, says so on the row and sends it anyway. Same rule as a
delivery minimum: the person holding the bag can see things the till cannot, and
a rule staff work around loses the record along with the sale.

Migration 9 adds the three columns, all null for every existing order and for
every shop that never sends a driver.

259 tests.

---

## 2026-08-17 — The unclicked screens, and the cheapest thing that was never switched on

Five screens had been shipped without anyone pressing a button on them. The
first move was not to write tests.

**Every view already declared `x:DataType`, and nothing was checking it.**
`AvaloniaUseCompiledBindingsByDefault` was never set, so those declarations only
fed IntelliSense: bindings still resolved by reflection at runtime and a wrong
name did nothing, quietly, on a screen nobody had opened. One line in the csproj
turns the whole class of fault into a build error across all six views at once —
far more coverage than any test written by hand, and it was free.

**It found a real one immediately.** The Orders list bound to
`PosOrder.OrderType`, a property removed in the schema rebuild when
`ServiceType` and `OrderChannel` were split. That line has been rendering a
blank on the orders list ever since, for two days of use, and nothing said so.

The other twenty-eight complaints were not faults: `{Binding DataContext.X,
ElementName=Root}` reaches the parent view model correctly at runtime, but the
compiler cannot know the type behind an element reference. They are now
`{Binding #Root.((vm:SettingsViewModel)DataContext).X}` — the cast is what makes
them checkable. This also resolves a note from the postcode-lookup work, which
recorded parent-relative command bindings as unverifiable; they are verifiable,
with the type spelled out.

**Then headless tests**, for what a compiler cannot reach. `ViewLoadTests`
builds each screen with the real `App`, real styles and real tokens, shows it and
lays it out.

`AppServices` no longer starts unless there is a desktop lifetime to show a
window in. That is right on its own terms — nothing should open the shop's
database, start print workers or poll a merchant's website when there is no till
on screen — and it is what lets the tests load the real application without
touching the live database.

**A limit, checked rather than assumed.** These tests catch anything that
*throws*. They do not catch a missing `StaticResource`: Avalonia leaves the
property at its default and logs. Proven by pointing a view at a brush that does
not exist and watching the test go green, then correcting the comment that had
claimed otherwise. Colours are still checked by looking.

265 tests.

---

## 2026-08-17 — The keyboard, and the machine in the corner

The last two items on the interface list.

### The keyboard

The screen has taken `88` and `3x88` since the interface work, but only through
an on-screen box. A shop with a numeric keypad should not have to reach for the
glass to use what it already has.

Digits from either number row build the entry, `*` is the quantity separator,
Enter adds, Escape clears — or closes the options panel first, that being the
thing most recently opened. `+` and `-` change the quantity on the selected
line, because those keys sit either side of Enter where the hand already is.

**The whole safety of this is one judgement about focus.** A cashier entering a
house number or a phone number is typing digits, and a layer that swallowed them
would put the customer's address into the dish-number box and drop it from the
ticket without a word. Focus is asked of the focused control rather than tracked
as state: state goes out of step with focus exactly once, and after that the
address field eats nothing for the rest of the shift. There is a test for every
key proving it does nothing while a field has the keyboard.

Handled tunnelling rather than bubbling, so a digit reaches the entry before a
`ListBox` on the way decides it meant type-ahead and moves the selection.
Modified keys are left alone — Ctrl+C on a till is still Ctrl+C.

### The print-only edition

`edition` in the bundle, one word, and the only difference between the two
products. `ShopEdition` is a string rather than an enum because it arrives from
a merchant's JSON and an unknown word must fall somewhere safe rather than throw
on a shop's first start — and it falls to the **full till**, deliberately: a typo
that quietly downgraded a paying shop would take their till away mid-service,
while a typo leaving a print-only machine with a Till tab it never opens costs
nobody a service.

**It lives in the tray.** That machine sits in a corner and nobody watches it. A
full-screen till would be minimised on the first day, and after that nobody could
tell whether it was still running — which is exactly the state that loses a shop
its orders. Closing the window hides it; quitting is a deliberate choice from the
tray menu, and the menu item says what quitting costs.

Its whole interface is two lights and one button, because everything on it is
either a state someone has to notice from across a room or the one thing that
goes wrong: orders arriving, printers ready, reprint.

Most of this already worked — the poller has never needed anyone signed in,
because `PosSession.Stamp` uses `Staff?.Id` and a web order needs no cashier.
What was missing was the shell, not the capability.

293 tests.

---

## 2026-08-30 — The cloud, decided; entitlements, started

The architecture is written down in [CLOUD.md](CLOUD.md). This entry records why
it went that way rather than what it says.

### Read first, decided after

Three repositories were read and none were touched: the ordering website, whose
`/api/menu` and `/api/print/epos/next` the till already lives off; the AI phone
project, which turns out to be a solved problem sitting on Railway with its own
Postgres and 33 written decisions; and this one.

The most useful thing found was that the phone product already frames itself the
way the cloud needs to: *a phone order is a web order arriving through a
different door*. It asks the website for every price and posts the same basket.
So AI phone orders need no new order concept here — only a channel tag on an
ingest that does not exist yet.

The second most useful was a warning. `/api/menu` is a **display copy**: it
strips the kitchen translations, drops sold-out dishes and drops hidden
categories. Everything a customer should not see, which is most of what a kitchen
ticket is made of. Anyone reaching for it as a menu source for the till should
read that route first.

### Why the cloud is one service and one pipe

The cloud starts as the entitlement authority and grows into ingest and sync, on
one Railway service with one Postgres, separate from the ordering website's
backend.

**Corrected the same day.** This first said the service would be its own
repository, by analogy with the AI phone project. Wrong analogy: that project
*consumes* an API it does not control and deliberately does not change, while
this service and the till *co-evolve one contract they both own*. Applying the
phone project's own three tests — blast radius, customers, runtime shape — gives
"together" on the first two, and the third is the test it says does not count.
It lives in `cloud/`, with Railway's root directory pointed at it. See
[CLOUD.md](CLOUD.md).

Two services would mean two auth schemes, two base URLs in Settings, two
deployments to keep in step and two things to be down, in exchange for nothing
that needs to scale separately yet.

The pipe is a **cursor**, not a webhook: `since=<seq>`. The change log already
approved for the till is `seq`-ordered, and "what has happened since `seq`" is
the same question whether the answer is polled, streamed or pushed. Deciding this
now, while only one thing flows through it, is what lets order ingest and
multi-terminal sync arrive later as event types rather than as plumbing.

Pull rather than push because a till sits behind whatever router a takeaway owns,
frequently behind CGNAT. Nothing reaches inward reliably. Polling survives a
router reboot without anyone noticing, and SSE can replace the transport later
without changing the question.

### Entitlements: what was built

`Entitlement`, `EntitlementState` and `EntitlementPolicy` — pure, no clock of
their own, no disk, no network. Verification and transport come next; the rules
came first because the rules are the risky part and they are the part that can be
tested without a server.

Four decisions worth keeping:

**The token is bound to `deviceId`.** Without it one shop's token unlocks every
install, and nothing misbehaves until the day somebody copies one. Tested rather
than trusted. The *shop* is deliberately not re-checked: a device is bound to a
shop at activation, and checking again locally buys no security while adding a
way for a shop renamed in the cloud to lock itself out.

**No path locks a till.** An expired token keeps its edition, its seats and its
features, and is marked stale so something visible can say so. A till that shut a
shop down at eight on a Saturday over a billing question would cost the merchant
a service and cost us the merchant. Cutting someone off is a decision a person
takes deliberately.

**No token falls to the bundle, not to `pos`.** A print-only machine that has
never reached the cloud stays print-only, because that is what was shipped. Only
a word that cannot be read at all falls the safe way to the full till, which is
the rule `ShopEdition.Normalise` already had.

**An empty feature list restricts nothing.** Only a populated list is an
allow-list. This is the surprising half and it is deliberate: switching
entitlements on changes nothing for any existing shop until somebody populates a
list, and an odd answer from the cloud cannot take a working feature away. Read
the other way round, the first payload that arrived with a field missing would
have bricked the estate.

The clock going backwards is ignored on purpose. Winding it back extends a token;
enforcing `issuedAt` would take a shop offline over a flat CMOS battery, which is
the more likely event by a wide margin.

### Not built yet

Signature verification, the HTTP client, the device identity, and the service
itself. The next step deliberately keeps the same order: the client's degrade
path first, tested by turning the real server off rather than by faking one —
same test, more truth.

317 tests.

### Changing a contract, and three things that are not the answer

Asked how a contract change would be handled once tills are in the field, and
whether the merchant could be sent a file, told to reinstall, or asked to copy
the data folder across. Each was checked against what the code already does.

**Copying the data folder is unnecessary and unsafe, and the code says both.**
`LocalPaths` puts everything under `%PROGRAMDATA%\RingOrder\EPOS`, outside the
install directory, so an uninstall and reinstall already keeps the data with
nobody doing anything. And an open SQLite database is three files: a merchant
copying `data.sqlite` while the till runs leaves `-wal` behind, which is the most
recent trading, and the copy looks healthy until a shift will not balance.
`BackupService` uses `VACUUM INTO` for exactly this reason — its own comment says
"unlike copying the [file]" — and `RestoreRequest` already handles `-wal` and
`-shm` and keeps the replaced database beside the new one.

**Reinstalling solves a problem auto-update already solved.** Getting new code
onto a machine is not the hard part of a contract change. Not knowing which shops
are behind is, and where auto-update is broken a manual reinstall usually fails
for the same underlying reason.

**Emailing a file** is right for a rare, deliberate event with a person on the
phone — an offline entitlement grant. As a repair mechanism it is weak, because
of forty shops phoned some do it, some do it wrong, and some are heard from in
three months.

What actually works is in CLOUD.md now: additive-only changes, unknown fields
ignored rather than rejected, **the client reporting its version on every
request**, and `/v2/` when a break is genuine. The version field is the one that
was missing from the earlier design and it is the cheapest: it turns "hopefully
everyone updated" into a list of who has not, which is the only thing that ever
makes it safe to delete the old server code.

When a client really is too old, the server says so and the client updates
itself — nobody phones a merchant. **Too old to sync never means too old to
trade.**

### Documents corrected rather than left to mislead

DEPLOYMENT.md described an offline signed licence with no server and no
activation, and — two sections later — a threat model where a mismatched Machine
ID means "will not activate" and an edited licence means "will not run". Those
contradicted each other before today and both contradict the design now.

Rewritten to say what is true: the rule that *nothing asks permission from a
server to sell food* did not change, the design found a way to keep it and have a
server too, and **nothing in the entitlement path can stop a till trading**. The
threat-model table now states the honest limit — someone who copies a whole
install gets a working till on whatever the bundle says — because that is
accepted rather than overlooked.

The old objection to hardware fingerprints was right and survives: the identity
is a random value generated at first run, not measured off the machine, so a
replaced PC simply re-activates. What changed is that a token is now bound to
that identity, without which one shop's token unlocks every install.

INTERFACE.md's "still to build" pointed only at packaging and the signed licence;
it now also carries the one thing from CLOUD.md that reaches a screen — an
expired entitlement is a **banner, not a dialog**, because it is a fact rather
than a failure that costs money. AGENTS.md lists CLOUD.md.

317 tests, unchanged — this was documentation and decisions, no code.

---

## 2026-08-30 — The entitlement client, end to end

The till side of [CLOUD.md](CLOUD.md) is built and wired. The service is not
written yet, deliberately: the risk in this feature is entirely in what the till
does when the cloud is absent, and that is testable without a cloud.

### Signed by Node, verified by C#

The fixtures in `fixtures/entitlement` are generated by
`node fixtures/entitlement/make-fixtures.mjs` and read by the C# tests. This is
not ceremony. Node signs ECDSA in DER by default and .NET verifies P1363 by
default — **two correct implementations that never interoperate** until somebody
pins the encoding. A round-trip test written in one language would have passed
happily and told us nothing.

The format is the JWT shape without the header: `base64url(payload).base64url(sig)`.
No header because a header exists to negotiate an algorithm, there is exactly one
algorithm here, and every `alg` confusion vulnerability ever written up came out
of that negotiation.

Serialiser options are pinned in the token file rather than taken from the shared
`JsonUtil`. This is a contract with software installed in shops; inheriting a
repository-wide serialiser would let somebody tidying a naming policy change what
a till in Birmingham can read.

### Two faults found by writing the tests

**Record equality compares a list by reference.** `EntitlementState` is a
positional record holding `IReadOnlyList<string> Features`, and
`EntitlementService` decides whether to raise its `Changed` event by comparing
the state before a refresh with the state after. Every refresh would have
announced a change — a banner redrawing itself daily, for the life of the
product, for no reason. Both records now compare by value, and the reason is on
the method so it cannot be tidied away.

**The service logged straight to `AppLog`,** which writes to
`LocalPaths.LogDirectory`. A test of the "token belongs to another machine" path
would have written into a merchant's live shop folder — the defect this project
has already had twice. The logger is now injected, following the
`AppLog.For(area)` convention the print queue and caller ID already use.

### Decisions worth keeping

**The device identity is random, not measured.** `Guid.NewGuid()` at first use,
stored in `settings`. The old objection to hardware fingerprints in DEPLOYMENT.md
was right and is preserved: a fingerprint revokes itself when a merchant replaces
a dying PC. Because the identity lives under `%PROGRAMDATA%` it survives an
uninstall and reinstall, so repairing an installation does not mean reactivating
a machine. A new machine simply activates again; a copied token is useless on the
machine it was copied to, which is all this has to achieve.

**`EntitlementKeys.Production` ships empty.** With no key nothing verifies and
every till falls back to its bundle, which is the documented behaviour for a shop
that has never reached the cloud. A build that shipped before the key existed
would therefore behave correctly rather than mysteriously. The development key —
whose private half is in the repository — is deliberately not in that list, and a
test holds it out, because a build that trusted it would accept a token anybody
could mint.

**A cloud address without credentials attempts nothing and says nothing.** That
is a shop provisioned without a cloud key, which is every shop today. It must not
produce a log line every day for the rest of its life, so it is pinned by a test
asserting the log is empty.

**Nothing is on the path to opening the till.** The state is resolved from disk
synchronously; the ask happens afterwards on a background task whose failure is
swallowed. `RefreshInBackground` wraps the client — which does not throw — in a
try/catch anyway, because a fault there must not be able to take down a shop.

### Checked on real data

The app was run rather than only built. On the live development database it
created `cloud.device-id`, recorded one refresh attempt, wrote **no** cloud lines
to the log, and opened the till exactly as before — which is the whole
requirement for a shop that has not bought anything from us yet.

351 tests.

---

## 2026-08-30 — The service, and the free window used once

`cloud/` exists: Node 24, one dependency, 34 tests, no build step. It follows the
AI phone project's conventions — native TypeScript, `node --test`, no framework —
because two services maintained by the same person should not need two habits.

### The endpoint was renamed before anyone could depend on it

The client shipped this morning called `POST /v1/entitlement`. It now calls
`POST /v1/sync`, which is what CLOUD.md said the shape should be: **the one call
a till makes on a schedule**, with order ingest and the change log arriving later
in that same answer as extra fields rather than as new plumbing.

That rename cost one line because **no till is installed anywhere**. It is the
free window the same document describes, used deliberately rather than
discovered. After the first real merchant install the same change would have been
a `/v2/` and two versions to run.

No speculative fields were added for the things that do not exist yet. Forward
compatibility means they can be added when they are real; empty placeholders
would only be a promise nobody has to keep.

### Two rules the service keeps that are easy to get backwards

**A known device is never refused for commercial reasons.** A shop that stops
paying has its row changed and is *told what it now has*; the till degrades to
exactly what we decided it keeps. Refusing outright surrenders that control and
lands the change thirty days later, on a day nobody chose. The single exception
is a device whose shop has been deleted, which is a deliberate act on our side.

**Activation is idempotent, and that is a recovery path rather than laxity.** A
till whose connection dropped between the service's answer and its own write
holds an activation key and no secret. Asking again is its only way out;
refusing a second activation would strand that machine permanently.

### Detail worth not rediscovering

`dsaEncoding: "ieee-p1363"` is load-bearing. Node signs ECDSA in DER by default
and .NET verifies P1363 by default — both correct, and they never interoperate.
A token signed without that line verifies nowhere and looks entirely normal until
a till rejects it. The fixture generator now imports the real signer from
`cloud/src/tokens.ts` rather than keeping its own copy, so there is one
implementation to be wrong.

**ECDSA is randomised.** A test asserting that signing the same payload twice
gives the same token failed, correctly: a fresh nonce is drawn per signature.
Only the payload half is stable. Recorded in TESTING.md as well, because
regenerating the fixtures always rewrites every file and a diff there does not
mean anything changed.

**An unreadable client version is let through.** A till that cannot say what it
is is far more likely to be one we have not taught to report yet than an
attacker, and refusing it would take a shop's entitlement away over a missing
string. `MIN_CLIENT_VERSION` is also absent by default: a floor set casually cuts
off whoever has not updated.

**Secrets are a single SHA-256, not scrypt.** The difference from a password is
entropy — these are 32 random bytes we generate, so there is no dictionary to run
and a slow KDF would defend against nothing while making every shop's daily
refresh measurably slower.

### The schema's most important feature is what is not in it

Two tables, and no column holds an order, a customer or an amount. The absence is
the enforcement. The first cloud service is where that boundary either holds or
starts leaking, and adding such a column is a decision that belongs in CLOUD.md
with a reason.

351 C# tests, 34 TypeScript tests.

### The transport, and one assumption that should not have been one

`main.ts` was split into `server.ts` (the HTTP skin, taking a store) and
`main.ts` (the only file that knows about Postgres). The handlers were already
tested as functions; what was not tested was everything a till could do that is
*not* a well-formed request — the body cap, malformed JSON, wrong method, wrong
path — and the `cache-control: no-store` that stops an entitlement sitting in a
proxy and handing a shop somebody else's plan. Those now run over a real socket
on a port the operating system picks.

Then the assumption. Every claim so far about the two halves agreeing was about
the *crypto*, held by the fixtures. Nothing checked the **field names**.

`PostAsJsonAsync` serialises with web defaults, which happen to be camelCase.
"Happen to be" is not a contract: somebody passing explicit options, or a default
changing, would send `ShopId` to a service reading `shopId` — and every till in
the field would be refused for a reason no log would explain. It is exactly the
class of fault this whole design has been arguing about, and it was sitting
unasserted.

It was correct. It is now pinned, in both directions and including the paths, and
the test names the file on the other side.

354 C# tests, 41 TypeScript tests.

### Migrations moved into startup, after the manual step failed once

The service's schema was applied by hand for one day. The very first setup
created two tables with one column each, through a **Create table** button in
Railway's database console, because the query box on that screen runs `SELECT`
and the `CREATE TABLE` never went anywhere.

Nothing said so. Both tables existed with the right *names*, the console showed
them, and the service returned `500 internal error` on every endpoint that
touched them. It was found by probing the deployed service rather than by
reading the console — a well-formed request that reaches a query is a better
schema check than a screen that lists table names.

Migrations now run at startup before the port opens, the way the till's have
since its first release. Files in `migrations/` in filename order, one
transaction each, recorded in `schema_migrations`, guarded by a
`pg_advisory_lock` so two instances in a rolling deploy do not race.

**The trap worth writing down:** `CREATE TABLE IF NOT EXISTS` does nothing to a
table that already exists with the wrong columns, and reports success. The
recovery is to drop the bad tables and let the migration run — there is no
version of this that repairs itself, and a migration that tried to would be one
that could destroy a real table later.

---

## 2026-08-31 — The change log, and two gaps found by asking the obvious question

### The change log

`change_log`, migration 10, with a hash chain — the decisions are in
[CLOUD.md](CLOUD.md). Applied to the live development database on this machine:
schema 9 → 10, pre-migration backup taken, eleven columns, zero rows.

Zero rows because **nothing writes to it yet**. The table, the chain and the
repository exist; wiring the order, payment and shift writes into it is the next
step and has to be done carefully, since each one must go inside a transaction
that already exists.

The trap worth naming: `Append` takes the caller's open transaction, and that
overload is the one to use. An entry that commits when the change it describes
rolled back is worse than no entry, because it will be believed.

### Two gaps, found by a question rather than by a test

Asked why a merchant sees every module the moment they open the till, and why
activation happens after they have already signed in and gone looking through
Settings. Both were fair, and the second one was the serious one.

**`EntitlementState.Allows` had no callers.** The entitlement was fetched,
verified, cached and displayed — and gated nothing at all. Everything was
visible to everybody regardless of what the token said. The plumbing was
finished and the tap had never been connected.

`ShopFeatures` now names what is optional, the delivery board is **derived and
permitted** rather than only derived, and caller ID is checked before the serial
port opens so a setting left on through a downgrade does not quietly keep
working.

The rule that came out of this and belongs in AGENTS.md: **only optional modules
may ever be gated.** The feature list is an allow-list, so naming one module
denies every other — gate anything core and granting a shop "drivers" takes away
its ability to sell food.

**Activation was in the wrong place.** Buried in Settings, reachable only after
signing in, which has the order backwards: a till should know what it is before
it is used. In practice nobody would ever go and find it, and the machine would
run unconnected for its whole life.

There is now a first-run screen, asked once. It always offers "set up later",
and skipping trades normally — install day is exactly when a shop's internet is
most likely to be nothing at all. The distinction that keeps this from being the
lock the whole design refuses to be: **a shop that has been trading can never be
stopped; a machine that has never traded loses nothing by being asked once who
it belongs to.** Every card terminal on the market pairs on first boot and none
is thought of as locked.

376 tests.

### The change log is now written to

Orders, tenders and shifts. The table stopped being empty.

**The verb is derived rather than passed in.** `OrderRepository.Upsert` reads the
order's status and settled tenders inside its own transaction, before writing,
and works out whether this save is a `placed`, an `amended`, a `paid`, a `voided`
or a `refunded`. Callers were not changed and cannot forget — a log the callers
have to remember to write is a log with holes, and the hole is always the path
somebody added in a hurry.

**A draft writes nothing.** A ticket being typed is saved on nearly every
keystroke; four hundred amendments per order would bury everything worth reading.
It starts existing in the log when it is sent, held or paid.

**Every tender gets its own entry.** A split payment recorded only as a total
cannot be reconciled against a card terminal's own report, and reconciliation is
most of what this log is for.

Payloads are summaries rather than serialised aggregates, in pence. The order
model is going to grow — courses, seats, split bills — and an entry holding a
whole `PosOrder` would either freeze that shape or fill the log with versions of
it. Pence because a payload is hashed exactly as serialised, and a decimal
rendered differently by a future runtime would be a chain that stopped verifying
for reasons nobody could find.

Two failures while writing the tests, both my own test data rather than the code:
two tickets sharing an order number, then two sharing a line id. Worth noting
only because they are the constraints a real till has and a fixture forgets.

383 tests.

---

## 2026-08-31 — The change log leaves the machine

Entries now ride on the same `/v1/sync` call as the entitlement, which is what
[CLOUD.md](CLOUD.md) said the pipe would be for from the start. The decisions are
there; this records what it cost.

### The bug that would have made every entry look forged

`cloud/src/chain.ts` reimplements `ChangeChain`, and the two have to agree byte
for byte. The first attempt produced `Z` where .NET produces `+00:00`, and
`Date.toISOString()`'s three fractional digits where .NET writes seven.

Both spell the same instant. Only one hashes to what the till wrote — so every
entry from every shop would have arrived looking tampered with, and the alarm
this whole chain exists to raise would have been firing constantly and meaning
nothing.

It was caught by **printing the canonical string from the real C# implementation**
rather than reasoning about the format, and those printed constants are now the
test. This is the third time on this project that a cross-language format has
been wrong in a way that looks fine from one side; the pattern that catches it
every time is the same one — make one side produce, make the other verify, and
never let a test compare an implementation against itself.

### Two rules that decide what a gap means

**The watermark follows what the cloud says it stored, never what was sent.** A
lost answer costs a re-send rather than leaving a gap, and an entry the cloud
refused gets offered again: it is evidence, and holding it twice beats losing it.

**A refused log never stops a shop.** The entitlement comes back in the same
answer either way. Whoever needs to know is us, not the merchant standing at the
till at eight on a Saturday.

### Entries are stored verbatim, and that is not tidiness

`payload` and `at` go into Postgres as `TEXT`, not `JSONB` and not `TIMESTAMPTZ`.
Both of those reformat what they store — JSONB reorders keys and drops
whitespace, TIMESTAMPTZ re-spells an instant — and the bytes are what was hashed.
Stored the tidy way an entry could never be re-verified, and nothing anywhere
would have said so. A separate derived column carries the timestamp for querying.

### A gap that was there all along

Nothing ever refreshed after startup. `RefreshInBackground` was called once and
there was no timer, so a till left running for a week never asked again — the
daily refresh described in the docs had never actually happened. There is a tick
now, and `RefreshAsync` throttles itself, so most of them do nothing.

Two reasons to go: the entitlement is due, or there is a log waiting to leave.
**Nothing pending and nothing due means no request at all**, so a quiet shop
still calls once a day rather than two hundred and eighty-eight times.

389 C# tests, 81 TypeScript tests.

---

## 2026-08-31 — Chinese on a note printed as rubbish

Found on real hardware, not by a test: a kitchen ticket came off the printer with
the dish name correct and the note beneath it unreadable.

Two paths, and the contrast was three lines apart in the same loop. A dish's
translation goes through `KitchenLine`, which renders CJK as a bitmap. The note
went through `Line`, which emitted code-page bytes the printer cannot render.
Nothing failed — the ticket printed, it was simply wrong.

`Line` now rasterises CJK too, at a smaller size than a dish translation gets: a
note is meant to be read, not shouted. **The same defect was in two more places**
nobody had hit yet — the order-level comments and the receipt footer lines — and
one change fixed all three, because there was only ever one wrong path.

ASCII is untouched, and there is a test holding it that way: a ticket whose
columns shifted would be a worse bug than the one being fixed.

**And a trap the fix created and closed in the same commit.** `WriteRasterLine`
falls back to text when it cannot draw a bitmap, and its fallback called `Line`.
With `Line` now routing CJK into `WriteRasterLine`, the two would have called each
other until the stack ran out — on a kitchen ticket, mid-service. The fallbacks
write bytes directly now, and a test builds a ticket that would have hit it.

394 tests.

---

## 2026-08-31 — The bundle stops being a file somebody carries

The last manual step in setting up a till: copying `shop.ringpos.json` onto the
machine. Upload it on `/admin` from the shop's own row instead, and every till
belonging to that shop picks it up.

**Only the version rides on a sync.** It is the SHA-256, computed by the service
so it cannot be passed in and set to something that does not match the contents —
which would leave every till convinced it was already up to date. The bundle
itself comes from its own call, only when that version differs. A shop whose menu
has not changed downloads nothing, and a menu is by far the largest thing this
service holds.

**Applied at the next start, never mid-service.** A bundle replaces the whole
catalogue; doing that while somebody is ringing a sale takes the dishes out from
under their fingers. It lands in `profile/` and goes in at startup — the same
shape as a restore, and a till is restarted far more often than a menu changes.

Two rules that pull in opposite directions and both had to be kept:

- **The cloud's bundle applies even when the till already has a menu.** That is
  the point of it — a price changes, it is uploaded once, the estate follows.
- **A file placed by hand still only seeds an empty till**, exactly as it always
  did. A shop with no cloud behaves as before.

A bundle that will not import is recorded as applied anyway. Retrying at every
start for the rest of a shop's life would fill the log and fix nothing; the next
upload has a different version and gets a fresh attempt.

**Credentials are deliberately not in it**, and the omission is the decision.
Holding merchants' passwords to somebody else's systems, in exchange for saving
four lines of typing on one day of a shop's life, is not a trade worth making.
The website password and the lookup key are typed once in Settings.

The bundle path is injectable on `EntitlementService` for the same reason the
logger is: a test that wrote into the live profile folder would be replacing a
merchant's menu.

398 C# tests, 90 TypeScript tests.

---

## 2026-08-31 — Packaging, and a flaw only running it could show

### Velopack

`vpk` packs a self-contained build; a merchant downloads `Setup.exe` once and
never again. `tools/pack.ps1` runs the tests first — a build that ships without
them is the one that needed them.

The rule the whole thing is shaped around: **a till is never restarted while it
is running.** It checks hourly, downloads quietly, and applies at the *next
start*, in `Program.Main`, before a window exists — the only moment at which
restarting a till costs nobody anything. Tills get restarted; shops turn them
off, staff reboot them, Windows does it anyway. Waiting for that is slower than
forcing it and is the only version of this that is safe.

`VelopackApp.Build().Run()` goes first in `Main`, before the single-instance
mutex. An install, an uninstall and the first run after an update all re-enter
the executable with arguments to be handled and exited on, and taking the lock
during one of them would deadlock the installer against the till it is
installing.

`UpdateFeed.Url` is empty, which disables everything — the same shape as
`EntitlementKeys.Production`. A build shipped before the feed exists behaves
correctly rather than mysteriously. No signing yet, by decision: SmartScreen's
warning is a conversation with a merchant rather than a technical failure.

### What running it found

The change log was working end to end on the real machine — twenty-six entries
written, chained, sent, and accepted by the cloud. Reading them showed something
no test had asked about:

**Nine of twenty-one order entries said exactly what the entry before them
said.** A ticket is saved several times per action — on send, on print, on the
screen moving on — and each save wrote an `amended` whether or not anything had
changed. Forty-three per cent of an append-only table that goes to the cloud and
stays there.

An amendment identical to the last entry for that order is now dropped. Only
amendments: a `placed`, `paid`, `voided` or `refunded` is written whatever the
figures say, because the verb is the news and a void whose totals match the sale
before it is still the event the day turns on.

This is the second thing this week that only appeared by looking at real output
rather than at a test — the first was Chinese on a printed note. Both were
invisible from inside the code, and both were obvious within seconds of reading
what actually came out.

407 tests.
