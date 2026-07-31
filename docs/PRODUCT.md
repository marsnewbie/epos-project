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
| Menu | Full catalogue ops (Settings → Menu): categories, dishes, option groups/choices, 86 |
| Staff | Manager PIN (void / drawer / delete / re-import) |
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

1. Review totals — footer shows **Paid / Due** when any tender exists  
2. **Cash** — keypad with ⌫ / CLR / Exact (fills **balance due**); next digit overwrites Exact/£10/20/50  
3. Cash confirm: amount ≥ due → full pay + change + clear ticket; amount &lt; due → **partial pay**, ticket stays with remaining Due  
4. **Card** — settles remaining balance (use after partial cash for split tender)  
5. **Orders → Reopen (PIN)** — reopen a paid ticket to add dishes; previous payments kept; collect new balance  
6. Settings: kitchen-on-pay · front-on-pay · open drawer on cash

## Language (industry standard)

- **UI language** (top bar): switches chrome only — nav, buttons, filters, dialogs, status text. One language at a time (never EN+中文 on the same button).
- **Menu catalogue language**: independent. Sell shows English front name + kitchen Chinese subtitle. Kitchen print keeps EN + 中文. UI toggle does **not** rewrite dish names.

## Settings sections

Shop · **Menu operations** · Quick notes · Delivery fee · Hardware (printers, CID, test print) · Staff PIN · Shift today · Online (advanced URLs + a/u/p)

### Menu operations (counter catalogue)

Industry-standard three-pane editor (not browse-only):

1. **Categories** — create / rename / sort / hide from Sell / delete (empty only, PIN)
2. **Dishes** — create / edit / duplicate / 86 / delete (PIN); menu #, EN name, kitchen CN, price, category
3. **Option groups** — Single (radio/select) or Multi (checkbox min/max); optional **Show when** (conditional)
4. **Choices** — label, kitchen CN, **+£** (0 = free), default, available
5. **Save dish** persists JSON option groups to SQLite and refreshes Sell immediately  
6. **Re-import seed** requires PIN + confirm (destructive)

Sell modifiers enforce max on toggle; required/min/max validated before Add to ticket.

See [MENU_AND_WEBSITE.md](MENU_AND_WEBSITE.md) for field mapping.
