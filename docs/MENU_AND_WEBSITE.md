# Menu, cart logic, and the website repo

## Where to look (read-only)

Website path: `C:\Projects\magicwok-birmingham-website`  
Remote: https://github.com/marsnewbie/magicwoksite

| Need | Website location |
|------|------------------|
| Menu seed / real dishes | `src/data/seed/menu.ts`, Admin-exported live seed under `src/data/seed/live/` |
| Types: OptionGroup, CartLine, Order | `src/types/index.ts` |
| Client pricing / modifiers | `src/lib/menu/pricing.ts` |
| Server reprice | `src/lib/orders/reprice.ts` |
| Delivery quote | `src/lib/delivery/*`, `src/app/api/delivery/quote` |
| Kitchen print format | `src/lib/print/gcanyorder-format.ts` |
| Shop hours / settings shape | `src/types/index.ts` `StoreSettings`, Admin Store panel |

## What the website already got right (reuse concepts)

- **Option groups**: radio / checkbox, required, min/max, `showWhen` conditional options, `priceDelta`.
- **Line notes** + order-level notes.
- **Kitchen translations** (`itemTranslation`, `optionTranslation`) — customer site English-only; kitchen bilingual.
- **Collection vs delivery**, fulfilment slots, promo engine (import carefully; POS may simplify v1).
- **Server-side price trust** — POS must also never trust a hand-edited line total without rules.

## What EPOS must add (website does not have)

- Ad-hoc open items  
- Caller ID + customer address book UX  
- Quick-note button pads (no onion / extra spicy …)  
- Walk-in / eat-in  
- Cash drawer + multi-tender  
- Sold-out (86) at counter speed  
- Split kitchen/front printers  

## Import strategy

1. Prefer pull from live `GET /api/menu` (or admin export JSON) into local SQLite.  
2. Store option groups as-is where possible.  
3. Map website dietary icons optionally (display only).  
4. Keep a **Menu operations** screen (Settings → Menu) for full CRUD + last-import timestamp; re-import seed is PIN-gated.

## Pricing rule

POS Domain should recalculate line totals from base + selected modifiers (same spirit as website `repriceOrderLines`). Ad-hoc lines use staff-entered unit price × qty.
