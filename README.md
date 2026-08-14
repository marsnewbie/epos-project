# Magic Wok EPOS

Windows takeaway / restaurant front-of-house EPOS for Magic Wok (and future clone shops).

**Stack:** .NET 8 + Avalonia 11.2 desktop.  
**Data:** local SQLite at `%APPDATA%\RingOrder.Epos\data.sqlite`.  
**Online orders:** JSON pull `/api/print/epos/next` (same claim queue as handheld GcAnyOrder). Default base: `https://magicwoksite.vercel.app`. Operator chooses which device polls.  
**Website repo:** [`magicwok-birmingham-website`](https://github.com/marsnewbie/magicwoksite) — separate GitHub; do not push EPOS commits there.

## Quick start

```powershell
cd C:\Projects\magicwok-epos
dotnet restore
dotnet run --project src/RingOrder.Epos
```

Requirements: .NET 8 SDK, Windows x64, printer queue **GlPrinter80** for print tests.

First launch seeds the **full Magic Wok menu** (21 categories / ~179 items) from embedded live JSON, plus shop defaults and Online base URL.

## Smoke test checklist

1. **Sell** — browse categories, double-tap a dish (or pick options then *Add item*), use quick notes, Send kitchen / Pay cash / Pay card (manual).
2. **Settings** — confirm printer `GlPrinter80`, *Save + test print*, open drawer if wired.
3. **Settings → Online** — paste `a` / `u` / `p` from website Admin → Print (do not commit secrets). Optionally *Apply base URL*. Enable polling or use Online → *Poll once*.
4. **Online** — with credentials + a pending website order, *Poll once* should upsert locally, kitchen-print, then ack `printed`.
5. **Orders** — see today’s tickets; reprint kitchen / front.
6. **Customers** — save phone/address; *Simulate call* jumps to Sell with match.

## Docs

| Doc | Content |
|-----|---------|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Solution layout, HAL, local DB |
| [docs/PRODUCT.md](docs/PRODUCT.md) | POS modules, phone orders, ad-hoc, quick notes |
| [docs/HARDWARE.md](docs/HARDWARE.md) | Printers, cash drawer, caller ID, payment |
| [docs/ONLINE_ORDERS.md](docs/ONLINE_ORDERS.md) | Website getorder / callback integration |
| [docs/MENU_AND_WEBSITE.md](docs/MENU_AND_WEBSITE.md) | Menu import vs website cart |
| [docs/LESSONS_FROM_CLOUDPAS.md](docs/LESSONS_FROM_CLOUDPAS.md) | Pitfalls from earlier POS experiments |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Delivery slices |
| [docs/TESTING.md](docs/TESTING.md) | Detailed test steps |

## GitHub

https://github.com/marsnewbie/magicwokepos.git

## Reminder

When you need real Magic Wok menu, option groups, print credentials, or order-field shapes, **open the website repo read-only**:

`C:\Projects\magicwok-birmingham-website`

Useful paths there: `src/data/seed/live/`, `src/types/index.ts`, `src/lib/print/`, `docs/gcanyorder/`.
