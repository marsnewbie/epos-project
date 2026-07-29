# Product design (industry POS, not website checkout)

The customer website is a **self-service cart**. This EPOS is a **staff-operated front counter**. Do not copy website UX; inherit **traditional UK Chinese / takeaway POS** patterns, then modernise.

## Modules

| Module | Purpose |
|--------|---------|
| Sell | Fast order build: categories, dish #, modifiers, qty, quick notes |
| Ticket | Live basket on the right: order type, customer, **Send / Hold / Pay** (ticket stays after Send) |
| Orders | Today’s list (Unpaid / Held / Paid), open unpaid on Sell, reprint, void (PIN) |
| Online | Website orders: big Accepting ON/OFF; Advanced = poll/test |
| Customers | Phone book + addresses; start order; Caller ID match |
| Shift | Today tender summary (Settings → Shift) |
| Menu | Local catalogue, 86, price edit (Settings → Menu) |
| Staff | Manager PIN (void / drawer / 86) |
| Settings | Shop / Menu / Notes / Delivery / Hardware / Staff / Online (advanced) |

## Ticket lifecycle (P0)

1. **Draft** — building the ticket  
2. **Send kitchen** — persist + print; **ticket stays open** with SENT badge; lines marked sent  
3. **Add more** — new lines show NEW; **Send new / 补打** prints only unsent lines  
4. **Hold** — park with name/phone label; resume from Sell Held strip or Orders  
5. **Pay Cash / Card** — tender; optional kitchen-on-pay if not yet sent; front receipt; cash opens drawer  
6. **Void** — Orders → PIN + reason; optional VOID kitchen ticket  

Statuses: `Draft | Sent | Held | Paid | Voided` (plus Open/Completed for online).

## Order types (POS)

- **Collection** / **Delivery** (address required before Send/Pay) / **Walk-in** / **Eat-in** (table # required)

## Phone / Caller ID

1. CID or **Phone order** / Customers → Start order  
2. Match customer → name + address → Delivery or Collection ticket  

## Quick notes

Tap notes → bind to **selected or last line**. Editable in Settings → Quick notes (EN + kitchen CN).

## Pay flow

1. Review totals  
2. Cash (tendered + change / Exact) or Card (manual)  
3. Settings: **Also print kitchen on Pay if not yet sent** · **Print front on pay** · **Open drawer on cash**

## Settings sections

Shop · Menu/86 · Quick notes · Delivery fee · Hardware (printers, CID, test print) · Staff PIN · Shift today · Online (advanced URLs + a/u/p)

See [MENU_AND_WEBSITE.md](MENU_AND_WEBSITE.md) for field mapping.
