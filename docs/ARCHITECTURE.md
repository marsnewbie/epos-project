# Architecture

## The shape

One Windows desktop application. UI, hardware and the online poller run in one
process against a local SQLite database. There is no browser, no local web
server, and no second process to go wrong — an earlier experiment split the till
into a browser UI plus a localhost agent, and every support call was about the
half that had stopped.

```
RingOrder.Epos            Avalonia UI (MVVM), the only project that knows about screens
RingOrder.Epos.Domain     orders, menu, staff, shifts, pricing, the bundle model
RingOrder.Epos.Data       SQLite, migrations, repositories, bundle import
RingOrder.Epos.Hardware   printers, cash drawer, caller ID, payment terminal
RingOrder.Epos.Online     polling the shop's website for orders
```

Dependencies point one way: `Epos → Data → Domain`, and `Epos → Hardware →
Domain`. Domain knows about nothing. Hardware emits bytes and knows nothing
about SQLite.

## Local-first, and what that buys

The till is the system of record for its own trade. It opens, takes orders,
prints and reconciles with the network unplugged, because that is the state a
shop is in more often than anyone admits — a router reboots at 7pm and service
does not stop.

The consequence to hold on to: **anything remote is an input or an output, never
a dependency**. The website is a channel that delivers orders. Our own support
tooling reads a copy. Nothing asks permission from a server to sell food.

## Data

`%PROGRAMDATA%\RingOrder\EPOS\`

```
data.sqlite     the till's own record
profile/        the shop bundle it was provisioned from
backups/        pre-migration and scheduled copies
logs/           rolling application log
```

Machine-wide rather than per-user: a shop that signs into a second Windows
account must not find an empty till, and support needs one predictable path.
When an installer has left ProgramData read-only for a standard account, the app
falls back to the per-user path rather than refusing to start.

### Money

Integer pence in SQLite; `decimal` in the domain. .NET's `decimal` is exact base
ten, so the arithmetic is safe; SQLite's `REAL` is binary floating point, which
is not. Conversion rounds half away from zero — banker's rounding turns a
half-penny into a day that does not reconcile.

### Rows, not blobs

Order lines and payments are tables. "What sold this week" and "what did each
person take" are the first two questions an owner asks, and neither can be
answered from a JSON column. Option selections stay as JSON *inside* a line,
because nobody reports on them and they are meaningless apart from their line.

### Migrations

`SchemaMigrations.All` is an ordered list. The runner records what it applied and
takes a `VACUUM INTO` backup before touching a database that already has data.
Append-only once a release is in a shop; no "down" — fix forward or restore.

## Who is at the till

Sign-in is mandatory, PINs are PBKDF2 with a per-user salt, and every order and
payment carries `staff_id`, `shift_id`, `channel` and `terminal_id`.

Actions are gated on a `Permission`, not on a rank. Someone who already holds the
permission acts without a challenge; someone who does not gets a supervisor
override — the supervisor's PIN, the cashier's screen, both names in the audit
log. `PosSession` holds all of this and stamps orders exactly once, on first
save, so reopening yesterday's ticket does not move its money into today's
shift.

## Channels and service types

Two independent axes, and squashing them into one list is the mistake to avoid:

- **ServiceType** — `Collection | Delivery | EatIn`. How the customer gets the
  food. Decides what the ticket must have: an address, a table number, nothing.
- **OrderChannel** — `Counter | Phone | Web | Platform`. Where the order came
  from. Decides reporting, ticket banners, and which commission the owner pays.

A phone order can be either service type. A website order can be either. "Waiting"
is not a third thing — it is a collection order with the customer standing at the
counter, so it is a flag that prints `COLLECTION - WAITING`.

### Website orders

The shop's website exposes a claim queue. The till polls it, maps the payload to
a `PosOrder`, prints it on the shop's own printers and acknowledges. Orders are
idempotent on `online_external_id`; an acknowledgement that fails is retried
without reprinting, because a kitchen that receives the same ticket every four
seconds is a worse failure than a late acknowledgement.

Prices arrive priced. The till does not re-price them and holds no second price
list.

Marketplace orders (Uber Eats, Deliveroo, Just Eat) are entered by hand today and
carry `Channel = Platform` plus the provider name, so reports separate them from
the shop's own trade. The schema is ready for an adapter; the adapter is not
written.

## The menu

Categories, dishes, and a **shared** catalogue of option groups. A dish links to
a group; the link carries the position and the conditional reveal, because two
dishes may present the same group differently. Editing "spice level" is one edit,
and the editor reports which other dishes it touched.

A dish's station and tax band may be left blank to follow its category, which is
the normal case — a shop re-plumbs a section, not forty dishes. Both are
**resolved** when a line is added and stored on the line: re-routing the menu
next week must not rewrite what last week's ticket said, and a line carrying a
null station would match no routing rule at all.

## VAT

UK retail prices include VAT, so the arithmetic runs backwards from the gross: a
£6.00 dish at 20% is £5.00 net and £1.00 VAT, not £6.00 plus £1.20. The other
direction overstates a shop's takings by a fifth.

A dish takes its band from its category unless it sets its own; delivery follows
the shop's default band; an order-level discount is apportioned across the lines
before VAT is worked out, because a discount reduces every band it touches.

**Nothing about VAT is printed unless the shop has entered a VAT number.** Most
small takeaways are below the registration threshold, and a receipt claiming VAT
from a business that cannot charge it is worse than one that says nothing.

`TaxCalculator` is pure, and its tests include the property that matters on a
receipt: net plus VAT reconstructs the gross, for every penny from 1p to £50.

## Places and people

`addresses` holds doors; `customer_addresses` links a person to one. They are
separate tables because they are separate things, and keeping them apart pays off
four ways.

**Deduplication.** A door is stored once however many customers live behind it —
flatmates ordering separately, a household that changed its number — matched on
`AddressFingerprint`, which reduces "Flat 2, 14 Bristol Rd." and
"FLAT 2  14 BRISTOL RD" to one identity by keeping only letters and digits.

**Several addresses per customer**, each with its own label and its own note for
the driver, and exactly one default.

**A place can be enriched.** An address first typed by hand gains coordinates the
first time a lookup covers the same door, without a second row appearing.

**Erasure becomes possible.** A street with nobody attached is geography. The
*link* is the personal data, so removing a customer takes their links and their
notes while the shop keeps a delivery map that never named anybody — which is
what lets one operation satisfy both GDPR erasure and the HMRC duty to keep six
years of sales. See [PRIVACY.md](PRIVACY.md).

The till's own-history fallback reads `addresses`, not the phone book, so the
feature that makes postcode lookup useful without a paid provider never opens a
customer record to do it.

Addresses used to be a JSON blob on the customer row. Migration 5 creates the
tables and `AddressBackfill` moves the data across in C# — not in SQL, because
the fingerprint that decides whether two rows are the same door must be computed
by one piece of code, and an SQL approximation of it would create duplicates the
moment it disagreed by a punctuation mark. Each customer moves in its own
transaction and its blob is emptied as it goes, so the pass is resumable and
re-running it does nothing.

## Postcode lookup

Type a postcode, press Find, pick the house. The uncomfortable fact underneath is
that **there is no free source of UK house numbers**: every service that can list
front doors is reselling the Royal Mail Postcode Address File, and the Royal Mail
charges for it. What is genuinely free — postcodes.io, Ordnance Survey open data
— confirms a postcode exists and names the district, but has never heard of
number 12.

So the shop chooses, and **off is the default**. A till that quietly starts
spending a merchant's lookup credits is not a till they trust.

| Provider | Cost | Gives |
| --- | --- | --- |
| Off | — | nothing; addresses are typed |
| postcodes.io | free, no key | postcode is real, town, coordinates |
| getAddress.io | free tier, then paid | full addresses |
| Ideal Postcodes | ~3–4.5p per lookup, prepaid | full addresses |

**The cache is what makes a paid provider affordable.** A takeaway delivers
inside a few miles and serves the same streets for years — a couple of thousand
postcodes that never change. Every answer is stored forever, keyed on the
normalised postcode, so a lookup is charged once per postcode for the life of the
shop rather than once per phone call. Settings shows how many were reused, which
is the evidence a merchant wants when a bill arrives.

Normalising is therefore load-bearing, not tidiness: if `b296aa` and `B29 6AA`
are two cache keys, the shop pays twice for one house. `UkPostcode` packs,
uppercases and re-inserts the single space before the last three characters, and
everything downstream keys on that.

Three sources are tried in the order that costs least: the cache, then the
configured provider, then **the shop's own delivery history** — which knows the
regulars when nothing else is available, and is the whole answer for a shop that
never turns a provider on. A real answer from a provider always beats history.

Two rules hold everywhere. **Nothing blocks the address fields** — whatever comes
back, staff can ignore it and keep typing, because a lookup that stops an order
being taken has cost a sale to save keystrokes. And **only real answers are
cached**: "no such postcode" is permanent and worth keeping, but a timeout says
nothing about the postcode and caching it would make one bad minute permanent.

The API key lives in the till's own database and in `secrets.json` at
provisioning — never in the shop bundle, which gets emailed and copied around. A
leaked key spends someone else's money.

## Printing

Four layers, and the separation is what lets a shop have four printers of three
different kinds without any of it reaching the sale.

**Transport** — Windows queue (USB and anything with a driver), raw TCP 9100
(network, and the only kind that can be asked whether it has paper), serial
(including a paired Bluetooth printer's virtual COM port), and file for support.
Kitchen printers should be wired: Bluetooth drops, sleeps and loses its pairing,
and re-pairing during service is not a thing anyone should have to do.

**Devices** — a registry, not two names in settings. Each carries its transport,
address, paper width, encoding and whether the drawer hangs off it.

**Routing** — rules matching (document, print class, service type, channel) to a
device, with copies and a fallback. Rules are matched in order and *every* match
fires, because a dish printing at the wok and again on the packing bench is a
shop asking for two copies, not a conflict. `PrintRouting` is pure, so the rules
are testable without a printer — the only way they get tested often.

**Queue** — jobs are rows carrying their rendered bytes, with a worker per
device, retry with backoff, and recovery of anything a crash left half-printed.
Queueing cannot fail for want of paper, which is the point: a kitchen printer
switched off at 6pm must not stop the till taking money at 6:01.

One nuance worth stating because it looks like a contradiction. A ticket is
marked sent when it is **queued**, not when paper appears — waiting for paper
would let two people send the same lines twice while the first job retries. "The
paper is the truth" governs the *job's* status, which is what the reprint list
and the printer light read.

## What is deliberately absent

- **No ORM.** The schema is small, the queries are hand-written, and a till's
  data access should be readable at 2am.
- **No dependency injection container.** `AppServices` constructs everything once
  at startup. One process, one lifetime.
- **No second process, no local HTTP.** See the top of this file.
- **No cloud dependency for selling.** See local-first.
