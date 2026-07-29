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
4. **CASH** / **CARD** on a Sent unpaid ticket → pays, prints front (if enabled), cash opens drawer → new blank ticket  
5. **Orders** → filter **Unpaid** → select → **Open on Sell** → continue pay  
6. **Orders** → **VOID (PIN)** (default manager PIN `1234`) → reason → optional VOID kitchen ticket  

## P1 — Sell speed

1. Dish **#** box + Enter/Add → adds by menu number  
2. Quick notes bind to last/selected **unsent** line; prompt if empty ticket  
3. **Phone order** / Customers → **Start order** fills ticket  
4. **DEL** without address → Send/Pay blocked  
5. **TABLE** without table # → Send/Pay blocked  
6. Cash pad → Exact / £10/20/50 → change shown; tender amount stored  

## P2 — Settings

1. Left sections: Shop / Menu / Notes / Delivery / Hardware / Staff / Shift / Online  
2. **Menu**: search → edit price → Save; **86** toggles availability (Sell hides 86 items)  
3. **Quick notes**: add/edit → Save → appears on Sell  
4. **Hardware**: “Also print kitchen on Pay if not yet sent” matches Pay behaviour (Send always prints)  
5. **Staff**: change Manager PIN → Void/Drawer require new PIN  
6. **Shift**: today’s cash/card/online totals  

## P3 — Online + safety

1. **Online**: big **ONLINE ON/OFF**; Poll once / Test under **Advanced**  
2. Top **Drawer** requires Manager PIN  
3. **EN / 中文** switches nav + Sell action labels  

## Online poller (unchanged protocol)

1. Settings → Online: paste a/u/p → Save  
2. Online → ON → place website order → kitchen + ack (`ak=Accepted`)  
3. Do not run GcAnyOrder phone at the same time  

## Hardware

- Queue name exact: `GlPrinter80`  
- Test print: Settings → Hardware  
