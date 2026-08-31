# Working on RingOrder EPOS

A Windows till for takeaways and restaurants. **One signed binary is installed in
every shop; the entire difference between two merchants is one configuration
file.** Everything below follows from that.

This file is the orientation. The rules here are expensive to re-derive and have
each already cost something once.

## Read first

| Doc | What it settles |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the code is shaped, and which decisions are load-bearing |
| [docs/SHOP_BUNDLE.md](docs/SHOP_BUNDLE.md) | The configuration file, and how a new shop goes live |
| [docs/INTERFACE.md](docs/INTERFACE.md) | Interface and interaction rules, and the reasoning behind them |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Packaging, updates, backup, and supporting a shop remotely |
| [docs/CLOUD.md](docs/CLOUD.md) | What runs off the till: entitlements, ingest, sync — and what may never depend on it |
| [docs/PRIVACY.md](docs/PRIVACY.md) | Personal data: what is held, for how long, and what erasure removes |
| [docs/TESTING.md](docs/TESTING.md) | What to run, and what to check by hand |
| [docs/WORKLOG.md](docs/WORKLOG.md) | What changed and why, newest last |

## The rules that matter most

**Shops differ by their bundle, never by code.** If something can only be done
for one merchant by editing a source file, that is a missing setting — say so
rather than special-casing a shop. The same rule governs our sibling web
product, for the same reason: the second copy of anything is the one nobody
keeps in step.

**Nothing about a shop is compiled in.** No embedded menus, no default shop name,
no default website URL. A till with no bundle says it needs setting up. This was
not always true, and undoing it cost a day.

**The till owns its data.** It works with no internet, no website and no account.
Web and marketplace orders are an *input channel*, nothing more. There is one
price list — orders that arrive from elsewhere carry their own prices and are
printed and recorded, never re-priced here.

**Money is INTEGER pence in SQLite.** `decimal` is exact in .NET and stays in the
domain; SQLite's `REAL` is binary floating point and a till whose day does not
add up is not a till. Rounding is half away from zero, never banker's.

**Every order and payment carries who and when**: `staff_id`, `shift_id`,
`channel`, `terminal_id`. This is why sign-in is mandatory. Anything that takes
money and cannot name the person who took it is unfinished.

**Migrations are append-only once a merchant has installed the release.** They
run in order, record themselves, and back the database up first. There is no
"down": a bad release is fixed by a new migration or restored from that backup.
While no merchant has a release, editing the migration in place is allowed and
better — say which you did in the worklog.

**Paper is the truth.** A print job is complete when the paper came out, not when
a status column changed. Acknowledgements must be idempotent — the failure mode
to design against is not a lost ticket, it is a printer producing the same
ticket forever.

**Never write to a merchant's shop folder from code.** `ringorder-epos-shops/` is
ours, git-ignored, and holds real menus and credentials.

**Do not touch the website repository.** `C:\Projects\magicwok-birmingham-website`
is a separate product. Read it if you need the print API's shape; never edit it,
never commit to it.

## Where things live

```
AGENTS.md                     this file
README.md                     what it is and how to run it
docs/                         the documents listed above
shops/demo/                   the one shop in the repo, used by tests
src/RingOrder.Epos/           Avalonia UI (MVVM)
src/RingOrder.Epos.Domain/    orders, menu, staff, shifts, the bundle model
src/RingOrder.Epos.Data/      SQLite, migrations, repositories, bundle import
src/RingOrder.Epos.Hardware/  printers, drawer, caller ID, payment terminal
src/RingOrder.Epos.Online/    website order polling, and the cloud client
tests/RingOrder.Epos.Tests/   xunit
cloud/                        the cloud service — TypeScript on Node 24
fixtures/entitlement/         tokens signed by the service, verified by the till
ringorder-epos-shops/         real merchant data — git-ignored, never committed
```

**This repository holds two programs in two languages.** The till is C#; the
cloud service in `cloud/` is TypeScript, deployed separately to Railway. They
live together because they **co-evolve one contract they both own**, and a change
to it has to land on both sides at once — see [docs/CLOUD.md](docs/CLOUD.md).

Data on a merchant's PC lives under `%PROGRAMDATA%\RingOrder\EPOS\`:
`data.sqlite`, `profile/`, `backups/`, `logs/`. Machine-wide, not per-user — a
shop signing into a second Windows account must not find an empty till.

## Before you finish

```bash
dotnet build RingOrder.Epos.sln
dotnet test RingOrder.Epos.sln
dotnet run --project src/RingOrder.Epos
```

Building is not enough. Run it: most of what breaks in a till is a flow, not a
compile error.

If you touched `cloud/`, or anything either side of the wire between it and the
till:

```bash
cd cloud && npm test && npm run typecheck
```

Add an entry to [docs/WORKLOG.md](docs/WORKLOG.md) for anything that changes
behaviour, data, or a decision. It is the trail a later session reads to
understand why the code looks like this.

## What is actually enforced

Read this before reporting something as broken. Each line below has already been
raised as a fault by someone reasoning from a name rather than from the code.

| Looks like a rule | What is true |
|---|---|
| `Permission` names map to job titles | They map to *actions*. A shop can re-grade roles without anyone hunting for role checks |
| A cashier is challenged for a PIN on every gated action | Only when they lack the permission. Someone who already holds it is not asked — challenging a manager for manager work teaches everyone to share a PIN |
| `OptionGroup.SortOrder` and `ShowWhen` belong to the group | They belong to the *dish's link* to it, and are filled in on load. Two dishes may place or reveal the same shared group differently |
| Editing an option group affects one dish | It affects every dish that references it. That is the point, and the editor says which ones |
| `MustChangePin` blocks sign-in | It is a flag and a prompt, not a gate. A shop mid-service must not be locked out by our provisioning |
| Deleting staff is possible | Only deactivation. Their name is on every order they took |
| `ServiceType` says where an order came from | `ServiceType` is how the customer gets the food; `OrderChannel` is where it came from. "Waiting" is neither — it is a collection order with the customer standing there |
| The bundle is read at runtime | It is a seed. After import the till owns the data and Settings is the source of truth |
| Postcode lookup is off because it is unfinished | It is off because it costs money. There is no free source of UK house numbers — every provider that has them licenses the Royal Mail address file — so switching it on is the merchant's decision, not our default |
| The address cache is a performance optimisation | It is a billing one. Each postcode is paid for once for the life of the shop instead of once per phone call, which is what makes a per-lookup provider viable at all |
| `Address` and `CustomerAddress` are the same thing | `Address` is a door and is shared between customers. `CustomerAddress` is one person's link to it, and carries the label and the driver note. The link is the personal data; the door is not |
| Erasing a customer should delete their addresses | It deletes the *links*. The place stays — a street with nobody attached is geography, and the shop keeps its delivery map |
| An erasure deletes the orders too | Orders keep their money and VAT and lose their identity. HMRC requires six years of sale records; GDPR erasure does not override a legal retention duty |
| Retention defaults to something sensible | It defaults to **0 — never remove**. The merchant is the data controller and a till that deleted their phone book on upgrade would be indefensible. Settings shows the count and the obligation; the click is theirs |
| A refund edits the order it came from | It never does. The sale keeps its lines, totals and VAT; the refund is a separate record beside it. A shop must be able to show both halves |
| A void and a refund are the same thing | A void says the sale never happened. A refund says it did and was reversed. Voiding a paid order does not return any money, and the till says so |
| Refunds appear in `PosOrder.Tenders` | They are in `PosOrder.Refunds`. As a negative tender a refund would push `BalanceDue` back up and let a settled sale be settled twice |
| Saving an order rewrites all its payment rows | The wipe is scoped to `is_refund = 0`. Money already handed back is not the caller's to delete |
| Delivery prefixes are matched as strings | They are matched on postcode *components* at four levels — area, district, sector, unit — most specific wins. **B47 never matches a B44 rule.** The space matters: `B44 0` is a sector, `B40` is a district |
| The delivery rules are ours to design | They are a port of the RingOrder website's `src/lib/delivery/`. A shop runs both, and two engines that disagree quote two prices for one order. Change them together or not at all |
| The below-minimum surcharge is the shortfall | It is a flat shop-level amount — the price of carrying a small order, matching the website and matching what arrives on a web order |
| Being under a zone minimum blocks the order | It warns and adds the flat surcharge if one is set. The person on the phone decides. A till staff work around loses the record along with the sale |
| A Z reading is a stored snapshot | It is rebuilt from the rows every time. The count, the expected figure and the variance are frozen on the shift row; the sales figures are a live view, for the same reason no total in this till is an accumulated column |
| Money lands in the shift that was open when it was taken | It lands in the shift the **order** was rung up in — `OrderRepository` binds `order.ShiftId` onto every payment. That is what stops a reopened ticket moving yesterday's money into today, and it means settling an old ticket changes a closed shift's sales figures |
| Takings and "value of settled sales" should match | They differ by whatever is part-paid on tickets still open. Money on an unsettled ticket is in the drawer and is not a sale yet |
| Shift VAT can be worked out from the day's gross | It is summed per order. VAT rounds on each sale because that is what the customer was charged; recomputing on the total rounds once instead of hundreds of times and puts the report a few pence away from the receipts |
| A card payment that did not come back was declined | It is `Unknown`, which is neither. Reading it as declined asks a customer who has already paid to pay again. The till queries the reference it chose; it **never** retries the sale |
| `ManualCardTerminal` is a placeholder | It is what most small takeaways actually do. It stays after an integration exists. It returns `Unknown` from a query because it genuinely cannot check, and inventing an approval would be the till asserting something it cannot know |
| `NMBR = P` is a phone number | `P` is withheld and `O` is unavailable. Stored as a number it puts a customer called "P" in the phone book and looks them up on every withheld call after |
| A caller's details arrive on one line | MDMF spreads them over several and sends `NAME` *after* `NMBR`, so a decoder that emits on the number throws the caller's name away. A call ends at a boundary, never at the number |
| Secrets in the database are safe because the database is local | The database leaves the shop in every nightly backup and in every copy sent to support. `OnlinePassword` and `AddressLookupApiKey` are DPAPI-encrypted at **machine** scope — user scope would lock out the second Windows account this product exists to serve |
| `x:DataType` on a view means bindings are checked | Only with `AvaloniaUseCompiledBindingsByDefault`, which is on. Without it the declaration fed IntelliSense and a typo failed silently. Reaching the parent VM from an item template needs the cast form — `#Root.((vm:X)DataContext).Cmd` — or it is uncheckable again |
| The headless view tests prove a screen looks right | They prove it does not *throw*. A missing `StaticResource` leaves the property at its default and still passes — checked, not assumed. Colour and layout are still checked by looking |
| A driver's round is worth what the orders total | Only the unpaid balances. A web or marketplace delivery was paid at checkout, so the driver carries food and not money — counting it would show every driver owing the shop their whole round |
| The dispatch board refuses to send an unfinished order | It warns and sends anyway. Same rule as a delivery minimum: the person holding the bag sees things the till cannot, and a rule staff work around loses the record |
| `StaffRole.Driver` is a kind of cashier | It grants nothing at the till. Drivers are staff because they carry the shop's cash and it must have a name on it, not because they operate the till |
| An undecryptable secret should be returned as-is | It comes back empty. DPAPI is machine-bound, so a database restored onto new hardware cannot read it — and handing back the blob would send it to the website as a password. The shop retypes the key |
| An expired entitlement locks the till | **No path in that code can lock a till.** It keeps its edition, its seats and its features and marks itself stale so a banner can say so. A till that shut a shop down at eight on a Saturday over a billing question would cost the merchant a service and cost us the merchant |
| An empty `features` list permits nothing | It restricts *nothing* — only a populated list is an allow-list. Read the other way round, the first payload arriving with a field missing would brick every till on the estate |
| A shop with no token gets the full till | It gets the edition in its **bundle**. Only a word nobody can read falls the safe way to `pos`, which is `ShopEdition.Normalise`'s existing rule |
| The entitlement is bound to the machine's hardware | To a **random identifier we generate at first run**. A fingerprint revokes itself when a merchant replaces a dying PC or plugs in a dock that moves a MAC address. A new machine simply activates again |
| `EntitlementKeys.Production` being empty is an oversight | It is the safe default until a production key exists: nothing verifies, every till falls back to its bundle, and a build shipped early behaves correctly rather than mysteriously. The development key is kept out of it deliberately — its private half is in this repository |
| The till waits for the cloud at startup | It resolves from disk, synchronously. The ask happens afterwards on a background task whose failure is invisible, and an unreachable service writes no log line at all for a shop that never had a cloud |
| Signing ECDSA in Node and verifying in .NET just works | Node signs DER by default, .NET verifies P1363 by default, and both are correct. `dsaEncoding: "ieee-p1363"` is load-bearing; without it a token verifies nowhere and looks entirely normal until a till rejects it |
| Regenerating the entitlement fixtures should produce identical files | ECDSA draws a fresh nonce per signature, so every file changes every time. Only the payload half of a token is stable |
| `change_log` replaces `audit_log` | They do different jobs. One holds a sentence a person reads; the other holds a payload a machine replays. Merging them makes one of the two worse |
| The hash chain makes the log unalterable | It makes an alteration **visible**. Anybody with the file can rebuild the whole chain — what they cannot do is change one entry and have it still verify. That is what an accountant or a fiscal authority actually asks for |
| The chain catches every tampering | Not a **truncated tail** — deleting the newest entry leaves nothing behind to disagree with it. The defence there is having already sent entries to the cloud, and the sync watermark noticing |
| `ChangeChain.Canonical` can be tidied | Never. Every chain ever written is verifiable only by the exact function that wrote it, so a neater version declares every shop's history broken. It is length-prefixed by UTF-8 byte count precisely so another language can reimplement it and agree |
| A log entry can be written after the change it describes | It goes in the **same transaction**. One that commits when the change rolled back is worse than no entry at all, because it will be believed |
| Anything in the till can be gated on an entitlement | Only the optional modules in `ShopFeatures`. The list is an allow-list, so naming one denies the rest — gate anything core and granting a shop "drivers" takes away its ability to sell food |
| The first-run setup screen is a lock | It always offers "set up later", and skipping trades normally. A shop that has been trading can never be stopped; a machine that has never traded loses nothing by being asked once who it belongs to |
| The setup screen nags until someone connects | Asked once, then remembered. A shop showing no tills on the estate page is the better reminder, because it reaches the person who can act |
| `Line` is the plain-text one and `KitchenLine` is the Chinese one | Both rasterise CJK. They did not, and a real kitchen ticket came off the printer with the dish name correct and the note beneath it as rubbish — the dish went through `KitchenLine`, the note through `Line`. ASCII is untouched by either, so columns and totals are byte for byte what they were |
| A bundle from the cloud is applied as soon as it arrives | It lands in `profile/` and goes in at the **next start**. A bundle replaces the whole catalogue, and doing that while somebody is ringing a sale takes the dishes out from under their fingers |
| Only an empty till imports a bundle | True of a file placed by hand, and deliberately not true of one from the cloud — a price changes, it is uploaded once, and every till belonging to that shop follows |
| Every save of an order writes a change-log entry | An amendment identical to the last one is dropped. A ticket is saved several times per action, and on a real shop's first evening nine of twenty-one entries said nothing new. Only `amended` is dropped — the other verbs are the news |
| An update is applied as soon as it is downloaded | It is applied at the **next start**, before a window exists. A till is never restarted while it is running: a restart at seven on a Saturday costs a merchant a service and costs us the merchant |
