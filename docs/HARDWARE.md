# Hardware

All devices go through `RingOrder.Epos.Hardware` interfaces. Settings bind concrete drivers.

## Receipt / kitchen printers

- Target verification printer queue name: **`GlPrinter80`** (80mm).
- Prefer **ESC/POS raw** to the Windows spooler; support dual routing:
  - `front` → customer receipt
  - `kitchen` → kitchen ticket (bilingual fields like website/GcAnyOrder)
- Same physical printer allowed for both channels during early testing.
- Kick cash drawer via ESC/POS pulse when drawer is wired through the printer.

## Cash drawer

- Default: ESC/POS open drawer on linked printer.
- Optional: direct COM/RJ11 driver later.
- Manager PIN for “Open drawer” without sale.

## Caller ID

- Provider interface: serial modem / USB CID / **Simulate** (dev).
- Settings: COM port, baud, enable flag.
- Event → UI toast + optional auto-open phone order.

## Payment terminal

- Interface: `StartSale(amount)`, `Cancel`, `Refund`.
- v1: **Cash** + **Manual card** (external terminal; POS only records tender).
- Do not label Manual as “integrated”. Slots reserved for Dojo / SumUp / Worldpay-class SDKs.

## Test checklist

1. Settings → Printers → select GlPrinter80 → **Print test page**.
2. **Open drawer** (if connected).
3. Kitchen sample ticket with Chinese + English lines.
4. Simulate Caller ID → number appears on Sell screen.
