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
