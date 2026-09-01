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

**Built.** The poller already printed without anyone signed in — `PosSession.Stamp`
uses `Staff?.Id` and a web order needs no cashier — so what was missing was the
shell, not the capability. `edition: "print"` now starts a tray icon and a
monitor window instead of the till: two lights (orders arriving, printers ready)
and a reprint button. Closing the window hides it; quitting is deliberate, from
the tray menu, and the menu item says what quitting costs.

An unrecognised edition falls to the **full till**. A typo that quietly
downgraded a paying shop would take their till away mid-service; one that leaves
a print-only machine with a Till tab it never opens costs nobody a service.

**Still needed from the installer:** auto-start at login, and restart-on-crash.
The application cannot arrange either for itself.

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

## Where releases live — decided

Two repositories, and the split is the whole point:

| | Holds | Visibility |
|---|---|---|
| `epos-project` | the source | private, or public — either is fine |
| `epos-releases` | releases only, no code | **public, always** |

`UpdateFeed.Repository` points at the second. A till therefore needs no
credential to find its updates, and making the source private changes nothing
about how updates work.

**The feed must move before the source does.** A till already installed looks
where its build was told to look; changing that afterwards leaves it checking a
repository it can no longer see, silently, for ever. Nothing is installed yet, so
today the ordering costs nothing — but it is the rule from here on.

### Why not object storage

Cloudflare R2 or S3 behind a CDN is where this ends up at scale, and R2's zero
egress is the reason: a full package is ~47 MB and every new install pulls one.

It is not needed yet, and the reason is deltas. From the second release onwards
Velopack ships only what changed — typically a few megabytes — so two hundred
shops taking a dozen updates a year is single-digit gigabytes. GitHub carries
that comfortably. Moving later is a one-line change from `GithubSource` to
`SimpleWebSource`.

### Building from a private source

Publishing to a public repository from a private one is the ordinary shape: a
GitHub Actions workflow in the source repo, with a token that can write releases
on `epos-releases` held as a repository secret. Not built — releases are packed
and uploaded by hand today, which is right at this size.

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

**Superseded 2026-08-30. A signed entitlement from our own service, cached for
thirty days.** The mechanism is specified in [CLOUD.md](CLOUD.md); this section
keeps the business framing and records what changed.

### What this used to say, and why it moved

It said: an offline signed licence file, no server, no activation. The reasoning
was that *nothing asks permission from a server to sell food*, and a licence
check needing the internet is a remote dependency the product had already
refused.

**That rule did not change — the design found a way to keep it and get a server
too.** The token is fetched in the background, cached, and honoured for thirty
days past its expiry; the till never waits on the network to open, and an
unreachable service is invisible. What the server buys that a file cannot is a
plan a merchant can change today rather than at reissue, and the first piece of
cloud the product owns.

The bought services were still rejected — Keygen (self-hostable CE), Cryptolens
(from ~€199), LicenseSpring (paid from ~$299/month). Keygen CE is a Rails app and
a Postgres for a problem that is three endpoints and two tables, and the others
price a problem we do not have. Ours runs in `cloud/` on the same Railway service
that will later carry order ingest.

### Still true

**It never stops a shop trading.** Nothing in the entitlement path can lock a
till: an expired token keeps its edition, its seats and its features and shows a
banner. A till that downed tools on a Friday evening would cost more trust than
the renewal it was chasing.

**There is no remote kill switch.** Cutting a merchant off is a thing a person
does deliberately, not a thing an expiry check does on a bad network.

**It is not on the receipt.** Paper is narrow and a customer does not care who
the till is licensed to. It appears in Settings → Support and in the diagnostics
export, which is where anyone asking is already looking.

**It is not bound to a hardware fingerprint.** That was right and it still is: a
fingerprint breaks when a merchant replaces a dying PC, and generates support
calls to defend against copying that is not a real risk in this market.

### What changed

**It is bound to a device identity.** A random identifier generated at first run
and held in the till's database — *not* derived from the hardware. Without it,
one shop's token unlocks every install.

The distinction is the whole point: a new PC gets a new identifier and
re-activates, so replacing a dying machine costs nothing, while a copied token is
useless on the machine it was copied to. The old objection was to fingerprints,
and it survives intact.

**Detect, do not enforce**, unchanged. A shop running two tills shows up in
`lastSeen` on the service; what happens next is a conversation, not a lockout.

## Protecting the local product — decided

### The threat model, stated so nobody argues with the wrong one

**Do not try to make the executable un-reverse-engineerable. That goal does not
exist.** Code on a customer's PC that a CPU can execute can be analysed, and
every hour spent chasing that is an hour not spent on the product.

What must actually work is smaller and entirely achievable:

| Who | What happens |
|---|---|
| A merchant copies their token to a second till | The device identity does not match; it is ignored, and that machine falls back to the bundle |
| Someone handy edits the token, 2028 → 2099 | The ECDSA signature no longer verifies; it is ignored, same fallback |
| A merchant copies the whole install to a friend | **They get a working till on whatever the bundle says.** Accepted — see below |

The third row is stated plainly because it is the honest limit of this design and
it is not an oversight. Nothing here refuses to run, so nothing here stops that
case; what it does is make a *paid tier* something only the service can grant. A
copied install is a shop that is visible in `lastSeen` if it ever connects and
receives nothing new if it does not.

A professional reverse engineer patching the binary is not a threat an early POS
should spend development time fighting, and neither is a merchant who has decided
to defraud us — the moat is the cloud product, not this executable.

### Six measures, and no more

0. **Updates come from a public repository that holds releases and nothing
   else** — `epos-releases`. The source is separate and may be private. A private
   feed would need an access token, and a token shipped inside every till is one
   anybody can extract; it would read the source as well as the releases.
   Splitting the two costs one empty repository and closes that off entirely.
1. **ECDSA signed entitlement** binding shop + device identity + expiry, fetched
   and cached — [CLOUD.md](CLOUD.md). The signing key never leaves the service,
   and a copy of it lives offline: losing it degrades every till on the estate
   within thirty days with no remedy.
2. **Release hygiene.** Customers get no source, no development configuration,
   no test tooling. **PDBs and debug symbols are not shipped.**
3. **Modest .NET obfuscation.** Not to be unbreakable — to make casual
   decompile-and-edit cost more than it is worth.
4. **DPAPI / Credential Manager for local secrets.** Tokens and device
   credentials that must live on the machine are not written to JSON in the
   clear.
5. **Real secrets stay server-side** — above all the licence signing key and any
   cloud credentials.
6. **No card data on the till.** The terminal and the acquirer handle the card;
   we keep the transaction reference, the status and the masked figure and
   nothing else. `PaymentResult` is already built this way — the till never sees
   a full PAN.

### Why the device identity is issued, not measured

An earlier draft of this section planned a hardware fingerprint and then listed
the operational damage it causes: hard disks die, merchants replace PCs, and a
NIC MAC address moves with a USB dock, so a licence bound to measured hardware
revokes itself when somebody plugs in a monitor.

All of that was correct, and it is the reason the identity is a **random value
generated at first run and stored in the till's database** rather than anything
read off the machine.

Two things follow, and both are cheaper than the fingerprint they replace:

- **A replaced PC re-activates.** New install, new identifier, one activation.
  There is no fingerprint to fail to match and nothing for support to re-issue in
  a hurry on a Monday morning.
- **The identity survives a reinstall on the same machine**, because it lives in
  `%PROGRAMDATA%\RingOrder\EPOS` with the rest of the data, which an uninstall
  does not touch.

### Point 4, as built

`OnlinePassword` and `AddressLookupApiKey` are encrypted at rest with Windows
DPAPI at **machine scope** — `LocalSecret`. Machine scope rather than user
scope because everything about this till is machine-wide: a second Windows
account must not find an empty till, and the background workers are not
necessarily running as whoever typed the key. The trade is that any account on
that PC can decrypt, which is right — the threat is the copy that leaves the
building, not the counter staff.

The stored form is self-describing (`dpapi:` prefix), so a value written before
this existed still reads and nothing needed a migration. A one-time pass at
startup encrypts anything still in the clear and logs that it did.

A secret that cannot be decrypted comes back **empty, never as ciphertext**.
DPAPI is bound to the machine, so a database restored onto different hardware
cannot read these back; the shop retypes the key. Handing back the undecryptable
blob would send it to the website as a password.

**Encrypting does not undo an exposure.** Anything already written in the clear
is in the backups that were taken before the change. When this shipped, the
development machine had the website password readable in all eight backups and
the Ideal Postcodes key — a credential that spends money per lookup — in four of
them. **Rotate any credential that was stored before this change**; the old
files are still readable.

## Where the money comes from — decided

Recorded here because it explains why the section above is deliberately light.

**RingOrder POS Core** — one payment, a two-year licence. Ordering, payment,
printing, drawer, Caller ID, terminal, tables, local reports. **Works completely
offline.** Renewed at two years.

**RingOrder Cloud** — a monthly subscription. Online ordering, remote reports,
multi-store, cloud backup, remote menu management, loyalty, CRM, and whatever
comes next.

**A shop that stops paying for Cloud keeps trading on the till.** That is the
point, and it is why no kill switch exists. The two products fail independently.

The moat is not the Windows application — it is the ecosystem around it. The
till is the part customers can hold, and the least valuable part to protect.
