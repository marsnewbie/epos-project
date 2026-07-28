# Roadmap

## Slice 1 — Foundation ✅

- Repo + docs  
- Avalonia shell with navigation  
- Settings (shop, GlPrinter80, online base URL)  
- Hardware HAL interfaces  

## Slice 2 — Core Sell ✅ (this delivery)

- Local Magic Wok menu seed (embedded live JSON → SQLite)  
- Domain pricing + modifiers + quick notes + ad-hoc  
- Ticket pay cash / manual card  
- Kitchen + front ESC/POS to GlPrinter80  
- Orders today + reprint  
- Customers phone book + CID simulate  
- Online poller + Goodcom parse + printed ack  

## Slice 3 — Online polish

- Stronger Goodcom edge-case parsing / promo lines  
- Sound / toast UX polish  
- Retry ack queue without reprint  

## Slice 4 — Phone desk

- Real serial Caller ID provider  
- UK postcode suggestions  

## Slice 5 — Polish

- Tables, shifts Z-report, dual printers, real terminal SDK, installer  
- Optional live `GET /api/menu` refresh (kitchen translations need admin export)  

Magic Wok is the first profile; keep shop-specific strings in settings/import, not hard-coded product logic.
