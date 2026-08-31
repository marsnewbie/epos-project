# The cloud

The till is the system of record. Everything here is an input to it or an output
from it, and **none of it may ever be required to take money.** A shop whose
internet is down trades exactly as well as one whose internet is up.

That rule is what makes the rest of this document safe to build.

## Where the orders come from today

```
AI Call (Railway) ──┐
                    ├──→ ringordersite (Vercel) ──→ print queue ──→ till (polls)
Web customer ───────┘
```

The website is the hub. A phone order is a web order that arrived through a
different door — the voice agent asks the website for every price and posts the
same basket to `POST /api/orders`. The till polls `/api/print/epos/next` and
prints whatever it claims.

**This works, and nothing here proposes breaking it.** The website stays the
authority for its own orders, and the till keeps polling it. What follows is
additive.

## Where they come from next

```
                     ┌──────────────────────────┐
Web orders ─────────→│                          │
AI phone orders ────→│   RingOrder POS Cloud    │──→ till(s)
Platform (later) ───→│   Railway + Postgres     │
                     │                          │
Back office (web) ←──│  entitlements · ingest   │
                     │  sync · history          │
                     └──────────────────────────┘
```

One inbound contract, many sources, each tagged with the channel it came from:
`web`, `ai-phone`, `platform`. The till already models `OrderChannel`, so a
tagged order needs no new concept — only a new door.

## One service, not several

The cloud is one Railway service with one Postgres, living in `cloud/` in **this
repository**. It starts as the entitlement authority and grows into ingest and
sync.

Splitting it into several services would mean two auth schemes, two base URLs in
the till's settings, two deployments to keep in step, and two things to be down.
At this scale that is all cost and no benefit. It stays one service until
something in it genuinely needs to scale on its own, and nothing does yet.

It is **separate from the ordering website's backend**, which is a different
product with its own concerns. Neither reads the other's database.

### Why the same repository, when the phone project chose a separate one

The AI phone project decided the opposite way, and said why: *"not because of
code taste — because of blast radius and customers."* Applying its own three
tests here gives the opposite answer.

| Its test | Voice vs website | Cloud vs till |
|---|---|---|
| Blast radius | Most website customers do not use voice | The till degrades by design, and the two **already deploy separately** — Velopack and Railway |
| Different product, different customers | Yes, two groups | **No. Two halves of one product, the same customers** |
| Different runtime shape | Yes | Yes — but that is the test its own decision dismisses |

The deciding difference is the contract. The phone project **consumes an API it
does not control and deliberately does not change**. The till and this service
**co-evolve one contract they both own**, and every change to it has to land on
both sides at once.

In one repository that is a single atomic commit. Across two it is two pull
requests and a window where the halves disagree — on every protocol change,
forever.

Railway's Root Directory points at `cloud/`, and its watch paths keep C# commits
from redeploying the service.

**Consuming someone else's stable contract → separate. Co-evolving your own →
together.**

### The contract fixtures

`fixtures/` holds sample payloads that **both sides' tests read**: the service
proves it emits them, the till proves it can parse them. A token the server can
sign but the till cannot read stops being possible to commit.

Sharing types between C# and TypeScript is not possible; sharing the bytes on the
wire is, and it catches the same faults. This only works in one repository —
across two it needs a submodule or a published package, which is enough
machinery that it stops being kept up.

## One protocol, one cursor

Everything the till needs from the cloud arrives through a single cursor-based
endpoint:

```
POST /v1/activate    one-time; activation key  → device secret + first token
POST /v1/sync        recurring; device secret  → token, and later a cursor,
                     inbound orders, and change-log events
GET  /healthz
```

`sync` rather than `entitlement`: it is the one call a till makes on a schedule,
and everything that arrives later joins that same answer as additional fields.
Unknown fields are ignored, so they can be added without breaking a till that has
not updated.

This is the decision most expensive to get wrong, so it is being made first,
while there is only one thing flowing through it.

**Why a cursor and not a webhook or a queue.** The change log approved for the
till is already `seq`-ordered, and "what has happened since `seq`" is the same
question whether the answer is polled, streamed or pushed. Getting the shape
right now means order ingest, multi-terminal sync and the AI history mirror all
arrive later as *new event types on an existing pipe* rather than as new
plumbing.

**Why pull and not push.** A till sits behind whatever router a takeaway owns,
often behind CGNAT, sometimes behind a captive portal. Nothing can reliably reach
inward. Polling works everywhere, needs no inbound port, and survives a router
reboot without anyone noticing. The transport can be upgraded to SSE later
without touching the protocol — the question the till asks does not change.

## Entitlements

The first thing on the pipe, because it is the smallest useful thing and it is a
real business need today: some merchants bought the full till, and some bought a
machine that only prints web orders.

The cloud answers with a **signed token**, cached locally:

```
{ v, shopId, deviceId, edition, features[], terminals, issuedAt, expiresAt }
```

**Signed, ECDSA P-256, verified against a public key in the binary.** The private
key never leaves the server. Extracting the binary yields the public key, which
is worth nothing.

**The token is bound to `deviceId`.** Without that field one shop's token unlocks
every install, and it is the single easiest thing to leave out.

### The three clocks

Confusing these is how a licensing system ends up needing manual work:

| Clock | Length | Who minds it |
|---|---|---|
| Token lifetime | 30 days | How long the cloud may be unreachable |
| Refresh interval | 24 hours | The till, silently |
| Billing period | whatever you sell | **The server only. The till never knows.** |

Refresh is a sliding window: every success writes a new 30-day expiry. In normal
running the token is never more than a day old, nobody renews anything by hand,
and the cloud can be down for a month before any shop notices.

### Degrade, never lock

An expired token means the till keeps the last feature set it was told about and
shows a banner. It does not stop.

A till that locks its own shop out at eight o'clock on a Saturday over a billing
question has cost the merchant a service and us the merchant. Cutting someone off
is a decision for a person to take deliberately, not for an expiry check to take
on a bad network.

This is the same instinct already written into `ShopEdition.Normalise`: an
unrecognised word gives the full till, because silently downgrading a paying shop
takes their livelihood away mid-service and the opposite mistake costs nobody.

### Restriction requires a positive statement

An **empty** feature list restricts nothing. Only a non-empty list is an
allow-list.

This is deliberate and it is the surprising half of the design: it means turning
the entitlement system on changes nothing for any existing shop until somebody
populates a list. The alternative reading — empty means nothing is permitted —
would brick every till the first time the cloud answered oddly.

### The fallback chain

```
1. signed token, device matches, current   → the token
2. signed token, device matches, expired   → the token, marked stale
3. no usable token                         → the bundle's own edition
4. bundle word unrecognised                → pos  (existing rule)
```

Step 3 is the bundle, **not** `pos`. A print-only machine that has never reached
the cloud stays print-only, because that is what we shipped it as. Only a word we
cannot read at all falls to the full till.

A merchant who unplugs the network keeps whatever the bundle says. That is
accepted: the bundle is a file we generate and deliver, so changing it is
deliberate rather than accidental, and the moat is the cloud product, not this
binary.

### The clock going backwards

Only expiry is enforced. A token whose `issuedAt` is in the future is still
honoured.

Winding the system clock back can extend a token, and that is the abuse this
ignores on purpose. A till PC with a flat CMOS battery boots in 2009, and
refusing to open on that would take a working shop offline over a fifty-pence
part. The clock is recorded and the oddity is logged; nothing is locked.

### What is built

The till side and the service. Order ingest, the change log and the back office
are not built; the protocol is shaped for them.

| Piece | Where |
|---|---|
| Meaning and fallback rules | `Domain/Entitlement.cs` — pure, no clock, no disk |
| Wire format and signature | `Online/EntitlementToken.cs` |
| Accepted public keys | `Online/EntitlementKeys.cs` — **empty until a production key exists** |
| Transport | `Online/EntitlementClient.cs` — never throws |
| Identity and cache | `Data/EntitlementStore.cs` — rows in `settings` |
| Wiring and refresh | `Services/EntitlementService.cs` |
| Shared contract fixtures | `fixtures/entitlement` — signed by the real service signer, verified by C# |
| The service | `cloud/` — Node 24, one dependency, 34 tests |

`App.axaml.cs` chooses its window from `Entitlement.Current`, resolved from disk,
so nothing waits on a network call to decide which product this is.

**A shop with no `cloud` block in its secrets notices nothing.** Verified on the
live development database: a device identity is created, one refresh is attempted
and returns "not configured", nothing is logged, and the till opens as before.

### The device identity

A random value created at first use and kept in the database, **not** measured
off the hardware. A fingerprint revokes itself when a merchant replaces a dying
PC or plugs in a dock that moves a MAC address, and it defends against copying
that is not a real risk in this trade.

Because it lives under `%PROGRAMDATA%` with the rest of the data, it survives an
uninstall and reinstall — repairing an installation does not mean reactivating
a machine.

## The change log

An append-only record of what happened, in `change_log`, with each entry
carrying the hash of the one before it.

Three separate things stand on it, which is why it is one table and not three:
cloud sync reads it by `seq`, a second terminal replays it, and anything
reasoning about a shop's history needs it. Current state says what is true now;
only a stream of events says what went on, and everything worth predicting lives
in the second one.

**It is an outbox, not an event store.** The tables remain the truth and this
records what changed them. Full event sourcing — the log as truth, every table a
projection — would be a rewrite of every repository for a benefit this product
does not need.

### It is not the audit log

`audit_log` holds a sentence for a person to read. `change_log` holds a payload
a machine can replay. Merging them would make one of the two jobs worse, so they
stay apart.

### Why the hash chain has to exist now

Each entry hashes its predecessor, so altering or removing anything invalidates
every entry after it.

That does not make the log unalterable — anybody with the file can rebuild the
whole chain. It makes an alteration **visible**, which is what an accountant, an
insurer or a fiscal authority actually asks for.

**A chain added later can only attest to what happened after it was added**, and
the day somebody asks is a day about the past. Germany's KassenSichV, Italy, and
France's NF525 all require a tamper-evident journal and none can be satisfied
retrospectively. Two columns now; impossible afterwards.

The one thing the chain cannot see is a **truncated tail** — deleting the newest
entry leaves nothing behind to disagree. The defence against that is having sent
entries to the cloud, not the chain, and the sync watermark notices when the
cloud was told about an entry that is no longer there.

### The canonical form is frozen

Each field is hashed as its UTF-8 **byte** length, a colon, then the field.
Length-prefixed rather than joined with a separator because a payload is
arbitrary JSON, and any character chosen as a delimiter is a character somebody
can put inside a field to make two different entries hash the same. Byte length
rather than character count so a reimplementation in TypeScript agrees.

**It must never change.** Every chain ever written is verifiable only by the
exact function that wrote it; a tidier version would declare every shop's history
broken.

Timestamps are normalised to UTC round-trip format before hashing, so an entry
verifies the same when it is read back in another time zone — which is what a
support copy of a database is.

### What is recorded, and what is not

Orders, their tenders, and shifts opening and closing. Every tender separately —
a split payment recorded only as a total cannot be reconciled against a card
terminal's own report.

**An amendment that says nothing new is not recorded.** A ticket is saved several
times per action — on send, on print, on the screen moving on — and left alone
that filled nearly half the log with entries nobody could tell apart. Measured on
a real shop's first evening: nine of twenty-one. Only amendments are dropped; a
`placed`, `paid`, `voided` or `refunded` is written whatever it says, because
the verb is the news.

**A draft is not recorded.** A ticket being typed is saved on nearly every
keystroke, and a log of four hundred amendments per order would bury the events
anybody cares about. An order starts existing in the log when it is sent, held or
paid: when it has become a thing that happened rather than a thing being typed.

**The verb is derived, not declared.** `OrderChangeVerb.For` works out `placed`,
`amended`, `paid`, `voided` or `refunded` from the state on disk against the
state being written, inside the same transaction. A log that callers have to
remember to write is a log with holes in it, and the hole is always the path
somebody added in a hurry — there is one write path for an order, so deriving it
there means it cannot be forgotten.

Payloads are summaries, not serialised aggregates. The order model is going to
grow — courses, seats, split bills — and an entry holding a whole `PosOrder`
would either freeze that shape or fill the log with versions of it. Money is
pence, because a payload is hashed exactly as it was serialised and a decimal
rendered differently by a future runtime would be a chain that stopped verifying.

### Appending

`ChangeLogRepository.Append` takes the caller's open transaction, and that is the
important overload. An entry that commits when the change it describes rolled
back is worse than no entry, because it will be believed. Reading the previous
hash inside the same transaction is what keeps the chain safe — SQLite serialises
writers, so nothing can slip in between the read and the insert.

Sync progress is a **watermark** in `settings`, not a column on each row, so the
table has no mutable field at all and "append-only" needs no exceptions
remembering.

## What a new installation sees first

A machine that has never been told which shop it is asks, once, before anything
else — the same first boot a card terminal or any other till on the market has.

**Skipping is always offered and is not a grudging escape hatch.** Install day is
exactly when a shop's internet is most likely to be a phone hotspot or nothing at
all, and the person holding the screwdriver may not have the code. A till that
refused to open until it phoned home would be the lock this whole design exists
not to be.

The distinction that keeps both rules true: **a shop that has been trading can
never be stopped, and a machine that has never traded loses nothing by being
asked once who it belongs to.**

It is asked once and then remembered. A merchant who skipped is trading, and a
prompt every morning teaches them to dismiss it — while a shop showing no tills
on the estate page is the better reminder, because it reaches the person who can
act on it.

## What is gated, and what may never be

`ShopFeatures` names the optional modules: today the delivery board and caller
ID. Nothing else is ever checked against an entitlement.

**Ringing a sale, taking money, closing a shift, the menu, staff and Settings are
never gated.** Two reasons, and the second is the one that bites:

1. A till that could hide its own Till tab can be bricked by a bad row in a
   database three hundred miles away.
2. The feature list is an **allow-list**, so naming one module denies every
   other — the moment anything core were gated, granting a shop "drivers" would
   take away its ability to sell food.

The delivery board is **derived and permitted**: the shop having staff graded as
drivers is what makes the board meaningful, and the entitlement is what makes it
something we sell. Either test alone gives the wrong answer.

Caller ID is checked before the serial port is opened, so a setting left switched
on through a downgrade does not quietly keep working.

## Sending the log up

Entries ride on the **same `/v1/sync` call** as the entitlement, which is what
this document said the pipe would be for: one question a till asks on a schedule,
with everything else joining that answer.

**Nothing pending means no request at all.** The entitlement is due once a day; a
backlog makes the till go sooner, at most every five minutes. A quiet shop that
took no orders calls once, not two hundred and eighty-eight times.

Five minutes rather than instantly because a busy shop would otherwise send after
every order; rather than daily because these are evidence, and entries sitting on
a till are entries somebody could still delete.

### This is the half the chain cannot do alone

Deleting the newest entry leaves nothing behind to disagree with it. But once the
cloud holds a shop's entries, a batch that **does not continue from what it
holds** says something was removed — and that is recorded against the device and
never cleared automatically, because a chain that broke once is a thing a person
looks at.

### The rules on each side

**The watermark follows what the cloud says it stored, never what was sent.** A
lost answer costs a re-send rather than leaving a gap, and an entry the cloud
refused is offered again — it is evidence, and holding it twice beats losing it.

**A refused log never stops a shop.** The entitlement comes back in the same
answer either way. Whoever needs to know is us, not the merchant standing at the
till.

**Entries are stored verbatim.** `payload` and `at` go into Postgres as `TEXT`,
not `JSONB` and not `TIMESTAMPTZ`, because both of those reformat — and the bytes
are what was hashed. A separate derived column carries the timestamp for
querying. Stored the tidy way, an entry could never be re-verified and nothing
would say so.

### The format is reimplemented, so it is pinned

`cloud/src/chain.ts` recomputes what `ChangeChain` computes, and the constants in
its test were **printed by the C# implementation** rather than worked out in
TypeScript.

The first attempt produced `Z` where .NET produces `+00:00`, and
`Date.toISOString()`'s three fractional digits where .NET writes seven. Both spell
the same instant; only one hashes to what the till wrote. Without the pinned
constants every entry from every shop would have arrived looking tampered with.

## The shop bundle

The step this removes: somebody copying a JSON file onto every machine.

Upload a bundle on `/admin`, from the shop's own row. Every till belonging to
that shop picks it up and applies it at its **next start**.

**Only the version travels on a sync.** It is the SHA-256 of the bundle,
computed by the service so it cannot be set to something that does not match the
contents — which would leave every till convinced it was up to date. The bundle
itself is fetched from `/v1/bundle` only when that version differs from what the
till has applied, so a shop whose menu has not changed downloads nothing.

### Applied at the next start, never mid-service

A bundle replaces the whole catalogue. Doing that while somebody is ringing a
sale would take the dishes out from under their fingers, so the download lands in
`profile/` and the import happens at startup — the same shape as a restore, and a
till is restarted far more often than a menu changes.

**The cloud's bundle applies even when the till already has a menu**, which is
the whole point: a price changes, it is uploaded once, and the estate follows. A
file placed by hand still only seeds an empty till, exactly as it always did.

A bundle that will not import is recorded as applied anyway. Retrying it at every
start for the rest of a shop's life would fill the log and fix nothing; the next
upload has a different version and gets a fresh attempt.

### Credentials are not in it

The bundle is a menu, printers, delivery zones and staff names — what makes a
till *this shop's* till. The website password and the postcode-lookup key are
typed once in Settings and stay out of the cloud.

The omission is the decision: holding merchants' passwords to somebody else's
systems, in exchange for saving four lines of typing on one day of a shop's life,
is not a trade worth making.

## Changing the contract

The contract is the shape of what crosses the wire: the token payload, the sync
envelope, the query parameters. It is not the code — it is what two things that
**deploy separately and update on their own schedules** have agreed to.

Everything else about a shipped till is easy to change. Screens, features, bugs
and the local schema all ride the auto-update, and a till that has not updated
yet simply carries on with the old version. Migrations are already append-only
with a `VACUUM INTO` backup taken before each one; that problem is solved.

The contract is the exception, because there is **no moment when both sides are
under your control**:

```
day 0    server v1  ←→  100 tills v1
day 1    server v2  ←→  100 tills v1          ← every deploy opens this
day 3    server v2  ←→   60 v2,  40 v1
day 30   server v2  ←→   98 v2,   2 v1        ← these two never update
```

The last two are a PC restored from an old image, a shop closed for a month, a
router blocking the update host. At a few hundred shops they always exist.

And a client broken by a contract change **cannot be repaired remotely**, because
the repair travels the road it can no longer use.

### The rules

1. **Additive only.** New fields are optional. Never rename, never remove, never
   change the type or meaning of a field that has shipped.
2. **Unknown fields are ignored, never rejected** — in both directions. This is
   what makes rule 1 safe. `System.Text.Json` does this by default; setting
   `UnmappedMemberHandling.Disallow` anywhere on this path would break every
   field-installed till the next time a field was added.
3. **Every request carries the client's version.** This turns "hopefully everyone
   updated" into a list of who has not, and it is the only thing that ever makes
   it safe to delete the old server code.
4. **A genuine break adds `/v2/` and leaves `/v1/` running** until rule 3 says
   nobody is on it.

### The window that is open now

None of this binds yet. **No till is in the field, so the contract is still free
to change**, and it stops being free at one identifiable moment: the first real
merchant installation.

That moment is worth choosing rather than discovering. The protocol should carry
a real order through a real service before it is installed anywhere, so that
getting it wrong once costs a rewrite instead of a `/v2/`.

Update this section when that install happens, and say when.

### Asking a till to update itself

When a client is too old to talk to, the server says so and the client updates
itself. Nobody phones a merchant.

**Too old to sync must never mean too old to trade.** The till keeps selling on
its cached entitlement, shows a banner, and updates in the background. The same
rule as every other degradation here.

### What is not the answer

**Uninstall and reinstall** solves getting new code onto a machine, which
auto-update already solved. It does nothing about the actual problem, which is
not knowing which shops are behind — and where auto-update is broken, a manual
reinstall usually fails for the same underlying reason.

**Emailing a merchant a file** is right for a rare, deliberate, human-in-the-loop
event such as an offline entitlement grant. It is a poor repair mechanism: of
forty shops phoned, some do it, some do it wrong, and some are heard from months
later.

**Asking a merchant to copy the data folder** is unnecessary and unsafe.
Unnecessary because `%PROGRAMDATA%\RingOrder\EPOS` is outside the install
directory, so an uninstall and reinstall already keeps the data with no action
from anyone. Unsafe because an open SQLite database is three files — copying
`data.sqlite` while the till runs leaves the contents of `-wal` behind, which is
the most recent trading, and the result looks perfectly healthy until a shift
fails to balance and nobody can say what went missing.

When a copy really is wanted, it is the backup in Settings: `BackupService` uses
`VACUUM INTO`, which reads through the write-ahead log, and `RestoreRequest`
swaps it in at the next start with the previous database kept beside it.

## What the cloud must never hold

The entitlement service stores `shopId`, `deviceId`, `edition`, `features`,
`terminals`, `lastSeen`. **No orders, no customers, no money** — not until there
is a decision, written down here, that says otherwise and says why.

The first cloud service is where that boundary either holds or starts leaking.

## The key nobody can lose

The signing private key lives in a Railway environment variable **and in an
offline backup somewhere else.**

If it is lost, no new token can be signed, and every till on the estate degrades
within thirty days with no remedy. It is the one failure that reaches every
customer at once.

Two public keys ship in the binary — the current one and its successor — so the
key can be rotated without a forced update to every shop. That costs two lines
now and is impossible to add on the day it is needed.

## AI, and what it actually asks of the architecture

Nearly every useful thing AI does for a restaurant runs in the cloud, not on the
till: forecasting, prep lists, natural-language reporting, waste prediction,
anomaly detection. None of it argues for a different client.

What it does ask for is two things, and both are cheap now and expensive later.

**History.** The change log is not only the multi-terminal foundation — it is the
entire raw material. Current state says what is true now; only the event stream
says what happened, and everything worth predicting lives in the second one.

**Hands, not just eyes.** An AI that can only read is a reporting tool. One that
can act — open a ticket, sell out a dish, reroute a printer — is an agent, and
that requires every till action to exist as a command that is idempotent,
authorised and audited, invoked by the UI and the agent through the same door.
Retrofitting that means rewriting the application; building it in means moving
logic out of view models as they are written.

## Not decided here

- Whether web orders eventually move off the website onto this ingest, or the two
  run side by side permanently. Both work; there is no need to choose yet.
- The merchant back office. It belongs in the browser, not on the till, but
  nothing about it is settled.
- Own hardware. The domain, data and online projects are already portable; the
  Windows binding is concentrated in `RingOrder.Epos.Hardware`, and mostly in
  `System.Drawing.Common`, which Skia replaces. The escape hatch exists, and is
  kept open by keeping platform code behind that project's interfaces.
