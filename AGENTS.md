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
docs/                         the seven documents above
shops/demo/                   the one shop in the repo, used by tests
src/RingOrder.Epos/           Avalonia UI (MVVM)
src/RingOrder.Epos.Domain/    orders, menu, staff, shifts, the bundle model
src/RingOrder.Epos.Data/      SQLite, migrations, repositories, bundle import
src/RingOrder.Epos.Hardware/  printers, drawer, caller ID, payment terminal
src/RingOrder.Epos.Online/    website order polling
tests/RingOrder.Epos.Tests/   xunit
ringorder-epos-shops/         real merchant data — git-ignored, never committed
```

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
