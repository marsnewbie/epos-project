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

Prices, tax class and print class are resolved when a line is added and stored on
the line. Re-pricing the menu next week must not rewrite what last week's ticket
said.

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
