# Getting it onto a merchant's PC, and keeping it there

The product is not finished when it runs. It is finished when a shop we have
never visited can be brought live in an afternoon, and a fix can reach two
hundred of them without ruining a Friday night.

Status is marked on every section: **decided** is settled and reflected in the
code, **proposed** is the current plan and still open, **not built** means
exactly that.

## The shape of a release — decided

One binary, self-contained, `win-x64`. Merchants must not need a .NET runtime
installed, and we must not care which one Windows Update left them.

One version number for every shop. Differences live in the bundle. A build made
for one merchant is the beginning of a fleet nobody can support.

## Packaging — proposed

The candidate is **Velopack**: delta updates, per-user install without
administrator rights, and a .NET desktop story that is actively maintained. The
alternatives considered were MSIX, which fights raw printer access and adds
packaging friction for no gain here, and a plain Inno Setup installer, which
leaves us writing our own updater.

Verify current terms and behaviour before committing — this is a decision, not a
fact, and the tooling moves.

## Code signing — proposed, and not optional

An unsigned executable is blocked by SmartScreen. The merchant's first
experience is a warning they were told to expect from viruses, and the call is
"your software is broken".

Since 2023, OV certificates require hardware tokens or a cloud HSM, which makes a
cloud signing service the practical route for a small company. **Azure Trusted
Signing** is the current candidate. Confirm eligibility, pricing and the
organisation-verification requirements before planning around it — some
programmes require a trading history.

Budget for this early. It gates the first real install.

## Update policy — decided in principle, not built

- **Two channels**: stable, and a beta ring that is us and one willing shop.
- **Staged rollout.** Never all shops at once. One bad kitchen-print release
  reaching two hundred shops on a Friday evening is the worst day this business
  can have.
- **A shop can be pinned** to a version — a merchant mid-dispute or mid-audit
  does not get changed underneath them.
- **Never during trading.** Updates apply inside a configured window (04:00–06:00
  by default) and only when no ticket is open. This is a hard rule in the trade
  and breaking it once loses a customer.

## What is already true

**Data lives at `%PROGRAMDATA%\RingOrder\EPOS\`** — machine-wide, one path for
every support script, with `profile/`, `backups/` and `logs/` beside the
database.

**Migrations back up first.** Any upgrade that changes the schema of a database
with data in it takes a `VACUUM INTO` copy into `backups/` before it starts.
`VACUUM INTO` reads through the write-ahead log, so unlike copying the file it
cannot capture a half-written page. This path has been exercised on a database
with a full menu in it.

**Provisioning is a file.** First run imports `profile/*.ringpos.json`. No
bundle and no data means the till says it needs setting up.

## Backup — not built

Nightly `VACUUM INTO` into `backups/`, with retention, plus an off-site copy for
shops that want it. `EposDb.BackupTo` already exists and is used by the migration
path; what is missing is the schedule and the restore tool.

The failure to design against is mundane: a cheap PC's disk dies and the shop
loses its order history and its customer phone book. Nobody thinks about this
until the morning it happens.

## Supporting a shop remotely — partly built

TeamViewer is installed on merchant machines, so support means someone sitting
at the shop's own desktop. What that person needs:

**Already there.** One predictable data path. A printer status light that says
whether Windows can open each queue. An import report naming exactly what a
bundle contained.

**Not built, and wanted in this order:**

1. **Rolling log file.** Errors currently go to the console, which is invisible
   on an installed copy. Everything else waits on this.
2. **A diagnostics screen** in Settings: version, shop, schema version, printer
   reachability, disk space, database integrity, last poll, last backup — and an
   "export diagnostics" button that produces one file to send us.
3. **Support scripts** shipped beside the app, so the same checks can be run
   without the UI: status, printer test, backup now, export diagnostics, set
   version.
4. **Heartbeat** — each till reporting version, printer health, last Z report and
   error count to a dashboard of ours. Opt-in, no personal data. Somewhere around
   twenty shops this stops being a luxury: it turns "an angry phone call" into
   "we already knew".

## The merchant's PC — not built, but write it down

A checklist that goes with every install, because Windows will otherwise
undermine the till on its own schedule:

- Auto-start the app on login; restart it if it dies
- Suppress Windows Update reboots during trading hours
- Disable sleep and USB selective suspend — a sleeping USB printer is a lost ticket
- Set the printer spooler to restart on failure
- Fixed IP or reservation for network printers
- Note which printer is which, physically, on the machine

## Hardware — built

The current till drives two Windows print queues. A real shop has more than two
devices and not all of them are USB.

**Transports.** Windows queue (works for USB, and for anything with a driver),
raw **TCP 9100** for network printers, and **serial** for Bluetooth adapters that
present a COM port. Kitchen printers should be wired: Bluetooth drops, sleeps and
loses its pairing, and a kitchen printer is not a device you want to re-pair
during service.

**A device registry** rather than two names in settings: transport, address,
paper width, encoding, whether CJK needs rasterising, whether the drawer hangs
off it.

**Routing by print class.** A dish carries a station; a rule sends
(station × service type × channel) to a device with a number of copies and a
template. Two kitchen printers and two front printers is an ordinary
configuration, and so is "the fryer gets its own copy".

**A real queue.** Per-device workers, retry with backoff, and jobs that survive a
restart, so one printer out of paper cannot stop the till taking money. With a
fallback device, so a dead kitchen printer means the front one prints the ticket
with a banner rather than the order being lost.

**Status.** TCP printers are asked whether they have paper and whether the cover
is open, and the answer appears when you test the printer in Settings. That is a
real difference from a till that finds out when the customer complains.

**What is left:** a reprint screen for jobs that ran out of attempts, and a route
editor in Settings — rules can be switched on and off there today, but adding one
for a third station still means editing the shop bundle.

## Licensing — undecided

Worth settling before the first paying shop: is there an expiry, is there a kill
switch, does the receipt say who it is licensed to. There are good arguments for
recording the licence and none for silently stopping a shop from trading.
