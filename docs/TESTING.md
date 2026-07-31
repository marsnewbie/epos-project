# Testing (counter POS lifecycle)

## Build

```powershell
cd C:\Projects\magicwok-epos
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
dotnet build MagicWok.Epos.sln -c Release
dotnet run --project src/MagicWok.Epos
```

## P0 — Ticket lifecycle (must pass)

1. **Sell** → add dishes → **SEND KITCHEN**  
   - Kitchen prints  
   - Ticket **stays** on the right with order # + **SENT**  
   - Lines show **SENT** badge (dimmed)  
2. Add another dish → button becomes **SEND NEW / 补打** → only new lines print (**ADDITIONS**)  
3. **HOLD** → enter name/phone → ticket clears; Held chip appears → tap to resume  
4. **CASH** keypad: Exact fills balance; ⌫ deletes; CLR clears; digit after Exact overwrites  
5. **Partial cash**: no final receipt; badge DUE; Paid/Due on ticket; optional **Interim receipt**; kitchen only if new unsent lines  
6. Kitchen ticket Payment line must say **PART PAID DUE £x** (not CASH as if settled)  
7. **Split**: partial cash then Card (balance) → final receipt on full pay  
8. Full pay → settlement overlay covers whole Sell (menu locked) → ticket stays visible → **Next order** blanks; leaving Sell also finishes settlement  
9. **Orders** list shows Due/Paid; Unpaid filter excludes fully-paid reopen leftovers  
10. **Reopen (PIN)** while Paid overlay was up → overlay clears, ticket + lines visible, badge REOPEN; add dish → Due; send/pay balance  
11. Held reprint kitchen must **not** un-hold the ticket  
12. After pay, **New/Clear** must never leave a stuck Paid screen (overlay gone; blank ticket usable)

## P1 — Sell speed

1. Dish **#** box + Enter/Add → adds by menu number  
2. Quick notes bind to last/selected **unsent** line; prompt if empty ticket  
3. **Phone order** / Customers → **Start order** fills ticket  
4. **DEL** without address → Send/Pay blocked  
5. **TABLE** without table # → Send/Pay blocked  
6. Cash pad → Exact / £10/20/50 → change shown; tender amount stored  

## P2 — Settings

1. Left sections: Shop / Menu / Notes / Delivery / Hardware / Staff / Shift / Online  
2. **Menu operations**  
   - **+ Category** → rename (Edit) → Hide/Show → Sell category strip updates  
   - **+ Dish** → set # / name / price → **Save dish** → appears on Sell  
   - **+ Group** (Single or Multi with min/max) → **+ Choice** with +£ or 0 → Save dish  
   - Conditional: second group **Show when** = first group choice (e.g. Curry → spice) → Sell reveals after pick  
   - Multi max: select up to max on Sell; cannot exceed (SMP-4 style)  
   - **86** hides from Sell; **Duplicate** copies groups with remapped ids  
   - Delete dish/category requires Manager PIN; category delete blocked if dishes remain  
3. **Quick notes**: add/edit → Save → appears on Sell  
4. **Hardware**: “Also print kitchen on Pay if not yet sent” matches Pay behaviour (Send always prints)  
5. **Staff**: change Manager PIN → Void/Drawer require new PIN  
6. **Shift**: today’s cash/card/online totals  

## P3 — Online + safety

1. **Online**: big **ONLINE ON/OFF**; Poll once / Test under **Advanced**  
2. Top **Drawer** requires Manager PIN  
3. **Language toggle** (top bar): switches UI chrome only (nav/buttons/filters) — one language at a time; dish names stay English + kitchen Chinese  
4. Light theme: white panels, dark text, readable under counter lighting  

## Online poller (unchanged protocol)

1. Settings → Online: paste a/u/p → Save  
2. Online → ON → place website order → kitchen + ack (`ak=Accepted`)  
3. Do not run GcAnyOrder phone at the same time  

## Hardware

- Queue name exact: `GlPrinter80`  
- Test print: Settings → Hardware  
