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
