# Testing (v1 core slice)

## Build

```powershell
cd C:\Projects\magicwok-epos
dotnet build MagicWok.Epos.sln -c Release
dotnet run --project src/MagicWok.Epos
```

Optional seed smoke (no UI):

```powershell
dotnet run --project tools/SmokeSeed/SmokeSeed.csproj -c Release
```

## Sell → kitchen

1. Open **Sell**. Left: category buttons. Centre: dish **tiles** (menu number + name + price). Right: ticket / till.
2. **Single tap** a dish to add. Dishes with options open a modifier panel — tap options, then **ADD TO TICKET**.
3. Quick notes strip under the grid (or select a ticket line first).
4. Order type: **COL / DEL / WAIT / TABLE** toggles. Delivery shows address fields.
5. Cash numpad (£10/£20/£50/Exact) → **CASH**; or **CARD**; or **SEND KITCHEN** early.
6. Ad-hoc: bottom-left **Ad-hoc / 临点**.

## Settings persistence

1. Change shop name, printer queue, Online base URL.
2. **Apply base URL** regenerates getorder / callback / printed.
3. Paste **a / u / p** from website Admin → Print (stored only in local SQLite settings — not in git).
4. **Save settings**, restart app → values remain.
5. **Re-import embedded Magic Wok menu** replaces local catalogue from embedded live JSON.

## Online poller

1. Ensure website Print is enabled and credentials match.
2. **Only one** consumer (EPOS *or* GcAnyOrder phone) should poll.
3. Online → **Poll once** (or Start poller). Empty queue → status “No order”.
4. Place a real website order → poll → local Online list → auto kitchen print → `printed` ack.
5. If ack fails after paper printed, use **Ack printed** — do not loop reprint forever.

## Orders / Customers

- **Orders**: today’s POS + online; reprint kitchen/front.
- **Customers**: save phone + address; **Simulate call** → Sell fills phone (and matched customer).

## Hardware notes

- Queue name must match Windows printer exactly: `GlPrinter80`.
- Encoding default `gb18030` (Chinese thermal); switch to `utf8` in Settings if needed.
- Drawer: ESC/POS pulse via the front/kitchen printer cable.
