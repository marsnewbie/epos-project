# Product design (industry POS, not website checkout)

The customer website is a **self-service cart**. This EPOS is a **staff-operated front counter**. Do not copy website UX; inherit **traditional UK Chinese / takeaway POS** patterns, then modernise.

## Modules

| Module | Purpose |
|--------|---------|
| Sell | Fast order build: categories, search by name/number, modifiers, qty |
| Ticket | Live basket: order type, customer, address, pay, send kitchen |
| Orders | Today’s list, reprint kitchen/front, void (PIN) |
| Online | Website orders: auto-fetch, sound, auto kitchen print |
| Customers | Phone book + addresses; Caller ID match |
| Shift | Open / cash drop / Z-report |
| Menu | Local catalogue, 86 (sold out), import from website |
| Staff | PIN roles: cashier / manager |
| Settings | All hardware + online + shop config |
| Reports | Day sales by tender / channel |

## Order types (POS)

- **Collection**
- **Delivery** (address + postcode + fee)
- **Walk-in / Wait** (customer waiting in shop)
- **Eat-in / Table** (optional module; can disable for pure takeaway)

Website only has collection + delivery — POS needs the extra shop-floor types.

## Phone / Caller ID flow (must-have)

1. Call comes in → CID shows number (or staff taps “Phone order”).
2. Match local customer → show name + saved addresses.
3. One-tap start **Delivery** or **Collection** ticket.
4. If new number → create customer quickly (name + phone).

## Address entry (delivery)

Traditional POS behaviour to preserve:

- Type postcode / partial address → **suggestions** (UK postcode lookup later; v1 can use local saved addresses + free text).
- Select address line → fills address + postcode fields.
- Delivery fee from local rules (miles/postcode bands — can mirror website settings when imported).
- “No address yet” allowed while building food; block Pay/Send until address valid for delivery.

## Ad-hoc items

Staff often sell something **not on the menu**:

- **Ad-hoc / Open item** button → name + price (+ optional kitchen translation).
- Prints on kitchen ticket like a normal line.
- Should appear in reports as “Ad-hoc”.

Website has no ad-hoc — POS must.

## Dish modifiers vs quick notes

### Structured modifiers (from menu)

Same idea as website `optionGroups` (radio/checkbox, price deltas, required/min/max, conditional `showWhen`). Import from website when possible.

### Quick note buttons (POS speed — critical)

One-tap kitchen instructions, **not** full free-text every time. Industry defaults (EN / kitchen CN):

| EN button | Kitchen CN example |
|-----------|-------------------|
| No onion | 不要葱 |
| No garlic | 不要蒜 |
| No coriander | 不要香菜 |
| Mild / Extra spicy | 少辣 / 多辣 |
| No chilli | 不要辣 |
| Less oil / salt | 少油 / 少盐 |
| Well done / Soft | 煎透 / 嫩一点 |
| Sauce separate | 酱汁分开 |
| Cutlery / No cutlery | 要餐具 / 不要餐具 |
| Urgent | 急单 |

Also: free-text note field for exceptions. Notes merge onto the line and print on kitchen ticket.

(Reference: earlier cloudpos `item-notes-modal` quick notes — keep the idea, polish UX.)

## Global order notes

Separate from line notes: “leave at door”, “doorbell broken”, “allergy: peanut” — print on ticket header/footer.

## Pay flow

1. Review totals (subtotal, delivery fee, discounts).
2. Tender: **Cash** / **Card (manual)** / later terminal SDK.
3. Cash → optional change calculator → **open cash drawer**.
4. Print **front** receipt (optional) + ensure **kitchen** already sent (or send on pay — shop preference in Settings: “Send kitchen on Send” vs “on Pay”).

Traditional UK takeaway often: **Send to kitchen early**, pay on collection — support both via Settings.

## Differences from magicwoksite checkout

| Topic | Website | EPOS |
|-------|---------|------|
| Who operates | Customer | Staff |
| Ad-hoc | No | Yes |
| Caller ID | No | Yes |
| Quick note pads | Rare (free text) | Yes |
| Tables / walk-in | No | Yes |
| Online inbound | Creates order | Pulls + prints |
| Payment | Checkout (cash/card intent) | Counter tenders + drawer |

See [MENU_AND_WEBSITE.md](MENU_AND_WEBSITE.md) for field mapping.
