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

## Editions — decided

Some merchants buy the till. Some install it only to print orders from their
RingOrder website and never ring a sale on it. **That is one binary and one
installer, not two products** — the same rule as the shop bundle, for the same
reason: the second build artefact is the one nobody keeps in step, and a
print-only shop that later buys the till must not have to uninstall anything.

The bundle carries an `edition`:

- **`print`** — the web-order monitor: the order feed, printer health, reprint,
  and the hardware, online and support settings. No Till, no shifts, no forced
  sign-in. Lives in the tray and starts with Windows, because it sits in a
  corner unattended and nobody is watching a full-screen window.
- **`pos`** — everything.

Most of this already works: the poller starts and prints without anyone signed
in, because `PosSession.Stamp` uses `Staff?.Id` and a web order needs no
cashier. What is missing is the shell, not the capability.

## Packaging — decided

**Velopack.** MIT, free for commercial use with no paid tier, delta updates,
per-user install without administrator rights, actively maintained for .NET
desktop.

Checked 2026-08-16, and two of these moved since this file was first written:

| Option | Licence | Cost | Verdict |
|---|---|---|---|
| **Velopack** | MIT | £0 | Chosen |
| Inno Setup | free | £0 | Viable, but we write the updater, the rollback and the staged rollout |
| WiX | **v6 requires an Open Source Maintenance Fee if you use it to generate revenue** | ask FireGiant | No longer the free option it was |
| MSIX | free | £0 | Still rejected: fights raw printer access |

### The ProgramData problem — decided

Velopack's per-user install is its main attraction, and it collides with
`LocalPaths`: the data root is `%PROGRAMDATA%\RingOrder\EPOS`, and an install
with no administrator rights may not be able to create or write it. The fallback
to `%LOCALAPPDATA%` then fires — which this codebase deliberately treats as a
fault worth a loud warning, because a per-user copy is invisible to support and
to a second Windows account.

So the smoothest install path triggers the failure mode we already decided was
bad. **First run elevates once**, does exactly one thing — create
`C:\ProgramData\RingOrder\EPOS` and grant Users write access — and everything
after that is per-user and silent.

## Code signing — proposed, and not optional

An unsigned executable is blocked by SmartScreen. The merchant's first
experience is a warning they were told to expect from viruses, and the call is
"your software is broken".

Since 2023, OV certificates require hardware tokens or a cloud HSM, which makes a
cloud signing service the practical route for a small company.

**Azure Trusted Signing is now called Azure Artifact Signing.** Checked
2026-08-16:

| Route | Cost | Notes |
|---|---|---|
| **Azure Artifact Signing** | Basic ~$9.99/mo (5,000 signatures), Premium ~$99.99/mo | Short-lived certificates, auto-renewed, auto-timestamped, private key never held by us. The pricing page publishes no figures — confirm in the Azure pricing calculator |
| OV certificate | ~£169–£220/yr **plus** HSM | FIPS 140 L2 key storage is mandatory: hardware token £70–£200, or cloud HSM (SSL.com eSigner ~$180/yr) |

Two things that changed the calculation:

- **From 2026-02-15 code signing certificates last at most one year**, so a
  self-held certificate means repeating identity validation annually. That
  favours the managed service.
- **Eligibility widened.** Microsoft's current quickstart lists Public Trust
  certificates as available to organisations in the US, Canada, the EU, **the
  UK**, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland,
  Norway and Israel. Individual developers are US/Canada only. Earlier guidance
  saying US/Canada organisations with three years of trading was narrower than
  what is published now.

**Still unconfirmed:** whether the three-year verifiable-history requirement
still applies to organisations. The current prerequisites do not restate it, but
recent Microsoft Q&A threads still show organisation validations failing on it.
Ask Microsoft rather than planning around either answer.

**Identity validation takes 1–20 business days**, possibly longer. It is a
scheduling item, not an engineering one: start it now, because no code waits on
it and the first real install does.

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

## Backup — built, except off-site

A `daily-<date>.sqlite` is written into `backups/` on the first startup of each
day and re-checked hourly, keeping fourteen days. The hourly re-check matters:
a shop that closes before midnight would never be caught by a 3am schedule, and
one that never turns the till off would never be caught by a startup-only one.

`VACUUM INTO` reads through the write-ahead log, so unlike copying the file it
cannot capture a half-written page. Three tests cover it, including taking a
backup while orders are being written.

The failure this exists for is mundane: a cheap PC's disk dies and the shop
loses its order history and its customer phone book. Nobody thinks about it
until the morning it happens.

### Restore — built

Settings → Support lists every backup with its date, size and what it is
("nightly", "taken automatically before a schema upgrade", "kept before an
earlier restore"), and puts one back.

Three things make it safe enough to hand a merchant:

- **The swap happens at the next start**, before anything opens the database.
  SQLite in WAL mode has two more files beside the main one and a pool of live
  connections; copying over that from inside the running process produces a
  database that is neither the backup nor the original. A restore is therefore a
  *request* — a marker file — and a crash between asking and starting changes
  nothing. The write-ahead files of the replaced database are deleted, or SQLite
  replays them over the restored one and quietly undoes half the restore.
- **The live database is kept first**, as `pre-restore-<timestamp>.sqlite`, so a
  restore can itself be undone. "I restored the wrong day" must not be the end
  of a shop's records.
- **The confirmation names the damage** — "43 orders worth £912.60 have been
  taken since it was made. They will be gone." A prompt that names no
  consequence is one people learn to click through.

**Still missing:** an off-site copy. Everything above protects against a mistake
and a corrupt file; none of it protects against the PC being stolen or the disk
dying, which is the failure the backups exist for in the first place.

## Supporting a shop remotely — partly built

TeamViewer is installed on merchant machines, so support means someone sitting
at the shop's own desktop. What that person needs:

**Already there.** One predictable data path. A printer status light that says
whether Windows can open each queue. An import report naming exactly what a
bundle contained.

**Built:**

- **A dated log file** under `logs/`, thirty days kept. Every startup, migration,
  provisioning report, print failure and crash lands there. Writes are
  synchronous under a lock — see the worklog for why the buffered version was a
  mistake.
- **Settings → Support**: version, machine, data folder, schema version, shop,
  printer health, queue depth, web-order status, last backup, and the log
  folder — on one screen, so nobody has to ask a merchant to read a version
  number down the phone. Plus **Export diagnostics**, which writes all of that
  and the recent log into one file to send us, and **Back up now**.
- **A reprint list** for tickets a printer gave up on, with the error against
  each. Deliberate, never automatic: a queue that retries forever is how a
  kitchen ends up with forty copies of one order.

- **Support scripts** — `tools/support/ringorder-support.ps1`, shipped beside the
  executable and signed with it. Everything it reports is also in Settings →
  Support; it exists for when that screen cannot be reached, which is exactly
  when it is needed: the app will not start, or a remote session is being driven
  by someone who has never seen the till.

  ```
  .\ringorder-support.ps1            status, backups, recent errors
  .\ringorder-support.ps1 Printers   queues, offline printers, stuck spooler jobs
  .\ringorder-support.ps1 Logs       tail the current log
  .\ringorder-support.ps1 Collect    one file to send us
  .\ringorder-support.ps1 Backup     copy the database now
  .\ringorder-support.ps1 Restore    queue a backup for the next start
  ```

  Read-only by default; the two commands that write say so and ask first.
  `Backup` warns when the till is running, because a plain file copy — unlike
  the app's `VACUUM INTO` — can catch a half-written page. `Restore` only writes
  the marker: the swap is always done by the till at startup, never by a script
  that cannot know who is holding the file.

  It flags the things that are silent and expensive: a stale nightly backup, a
  fallback to the per-user data folder, an offline printer, and jobs piled up in
  the Windows spooler.

**Still wanted:**

1. **Heartbeat** — each till reporting version, printer health, last Z report and
   error count to a dashboard of ours. Opt-in, no personal data. Somewhere around
   twenty shops this stops being a luxury: it turns "an angry phone call" into
   "we already knew". Needs a server that does not exist yet.
2. **An off-site backup copy.** See Backup above.

## The merchant's PC — not built, but write it down

A checklist that goes with every install, because Windows will otherwise
undermine the till on its own schedule:

- Auto-start the app on login; restart it if it dies. **Only one till per PC** —
  the app now refuses a second instance, because two of them sharing a database
  would double-print and disagree about the shift
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

**Configured in Settings, not by us.** A shop adding a fryer printer adds the
device, sets the section's station on the category, and adds a rule — no bundle
edit, no call to us. That is the test of whether the routing model is real.

## Licensing — decided, and deliberately slight

**Do not spend effort here.** A till is not where this business is defended.

The reasoning is worth stating because the obvious instinct is the wrong one. An
EPOS is a mature, well-understood product; nothing in this codebase is a secret,
and a competitor gains nothing from a copy of it. The till is **phase one**:
conventional features, done properly, on the merchant's own machine. What comes
later — and what is already true of web orders — is that **the parts worth
protecting live in the cloud**, where access is controlled by the service and
not by a file on a shop's PC. The till receives and prints.

So licensing here records who a machine belongs to. It is not a lock, it is a
label, and building it as anything more would be effort spent guarding the half
that does not need guarding.

**An offline signed licence file. No server, no activation, no kill switch.**

The cloud licensing services were priced and rejected on architecture rather
than on cost: Keygen (free Dev tier, self-hostable CE), Cryptolens (from ~€199),
LicenseSpring (free basic, paid from ~$299/month). All three assume the client
reports to a server. This till's first rule is that it sells food with the
router unplugged, and *nothing asks permission from a server to sell food*. A
licence check that needs the internet is a remote dependency the product has
already refused once.

Keygen CE is free and self-hostable, and is still a Rails app and a Postgres to
run — infrastructure for a problem we do not have.

### What it is

A JSON licence signed with **ECDSA P-256 / SHA-256** — `System.Security.Cryptography`,
no package, no service, no monthly cost. The public key is compiled in; the
private key stays with us. Roughly 150 lines.

It carries the merchant, the licence type, the issue date, an expiry, and the
**edition** — which is the right home for it. `edition` in the shop bundle is a
plain field a merchant can edit; in a signed licence it is not.

### What it does not do

**It never stops a shop trading.** An expired licence says so at startup and in
Settings; the till keeps taking money. A till that downed tools on a Friday
evening would cost more trust than the renewal it was chasing.

**It does not phone home, and there is no remote kill switch.**

**It is not on the receipt.** Paper is narrow and a customer does not care who
the till is licensed to. It appears in Settings → Support and in the diagnostics
export, which is where anyone asking the question is already looking.

**It is not bound to the machine.** A hardware fingerprint breaks when a
merchant replaces a dying PC — which happens — and generates support calls to
defend against copying that is not a real risk in this market. The opt-in
heartbeat would show a shop running two tills anyway: **detect, do not enforce.**
That costs nobody a service when it is wrong.

Settle before the first paying shop; nothing else waits on it. And keep it
small — if it grows past a signature check and a line in Settings, it has become
the thing this section says not to build.
