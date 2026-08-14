# RingOrder EPOS

A Windows till for takeaways and restaurants. Counter, phone, website and
marketplace orders on one screen; kitchen and receipt printing; cash, card,
shifts and staff.

**One signed binary is installed in every shop. The entire difference between two
merchants is one configuration file.** It works with no internet, no website and
no account — a shop that has bought only the till is a first-class customer.

**Stack:** .NET 8 + Avalonia 11, local SQLite.
**Repository:** https://github.com/marsnewbie/epos-project

## Run it

```bash
dotnet run --project src/RingOrder.Epos
```

A fresh machine has no shop. Copy a bundle in first:

```bash
cp shops/demo/shop.ringpos.json "$PROGRAMDATA/RingOrder/EPOS/profile/"
```

First run creates the database, applies migrations and imports the bundle. Sign
in with the seeded manager PIN — `1234` for the demo shop, which the staff list
will keep telling you to change.

Data lives in `%PROGRAMDATA%\RingOrder\EPOS\`: the database, the bundle it was
provisioned from, backups and logs.

## Docs

Start with **[AGENTS.md](AGENTS.md)** — the rules, the layout, and the things
that look like rules but are not.

| Doc | Content |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the code is shaped and which decisions are load-bearing |
| [docs/SHOP_BUNDLE.md](docs/SHOP_BUNDLE.md) | The configuration file, and putting a new shop live |
| [docs/INTERFACE.md](docs/INTERFACE.md) | Interface and interaction rules |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Packaging, updates, backup, remote support, hardware plan |
| [docs/TESTING.md](docs/TESTING.md) | What runs, and what to check by hand |
| [docs/WORKLOG.md](docs/WORKLOG.md) | What changed and why |

## Shop data

The repository carries the product and one demo shop. Real merchants live in
`ringorder-epos-shops/`, which is git-ignored and holds their menus, source
material and credentials. Back that folder up — ignored means unversioned.
