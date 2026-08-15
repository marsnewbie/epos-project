# Personal data in the till

Who holds what, why, for how long, and what happens when someone asks to be
forgotten. Written for two readers: whoever is changing the code, and whoever has
to answer a merchant asking "what does it actually do with my customers' data".

**The shop is the data controller.** RingOrder supplies the software; the merchant
decides what is collected and how long it is kept. Anything in here that reads
like a policy is a *default and a tool*, not a decision made on their behalf.

## What counts as personal data here

An address on its own is a building. Attached to a name, a phone number, or a
history of what somebody ordered, it identifies a person — the ICO gives names
and addresses as its plain example of personal data. That distinction is built
into the schema rather than left as a comment:

| Table | Holds | Personal? |
|---|---|---|
| `addresses` | street, town, postcode, coordinates | No — a place |
| `customer_addresses` | which customer lives where, label, driver notes | **Yes** — the link is the point |
| `customers` | name, phone, notes, last order date | **Yes** |
| `orders` | money, VAT, service type, plus name/phone/address as taken | **Yes**, until erased |
| `address_cache` | provider answers per postcode | No — nobody is named |

The split is what makes the rest of this document possible. Erasing a person
removes the links and leaves the streets, so a shop keeps a delivery map it never
needed a name to build.

## Why each thing is held

- **Name and phone** — to take the order, call back about it, and recognise a
  regular on caller ID.
- **Address and driver notes** — to deliver the food.
- **Order history** — to reprint a ticket, settle a dispute, and to file a VAT
  return.
- **Coordinates** — to price a delivery by distance or zone.

Nothing here is collected for marketing, and nothing leaves the till except a
postcode sent to the lookup provider the shop chose.

## What leaves the machine

Only a **postcode** — never a name, never a phone number, never a house number —
and only to the provider selected in Settings, and only when a member of staff
presses Find. Answers are cached locally, so a postcode the shop has looked up
before is not sent again.

With the lookup switched off, which is the default, nothing leaves at all.

## Retention

`CustomerRetentionMonths` counts **months since the last order**, not since the
record was created — a regular of ten years is the opposite of stale.

**It ships as 0, meaning nothing is removed automatically.** A till that deleted
a merchant's phone book on first upgrade would be indefensible, and the choice is
theirs to make. Settings → Customer data shows how many records are past any
period they try, states the obligation, and leaves the button unpressed. A second
switch, off by default, lets the sweep run at startup once they have seen what it
would take.

## Erasure

Settings → Customer data erases dormant records in bulk; the phone book has an
**Erase customer** button for an individual request, armed by the first press and
carried out by the second.

Either way, the same thing happens:

**Removed**

- the `customers` row — name, phone, notes
- every `customer_addresses` link, including the driver notes on it
- from that customer's orders: name, phone, delivery address, delivery postcode,
  the `customer_id`, and the raw `online_payload` a web order arrived in

**Kept**

- the orders themselves: lines, totals, VAT, service type, payment
- the `addresses` rows — places, with nobody attached to them any more

**Not touched, and worth knowing about**

- free-text order notes. "No MSG", "allergy: peanuts" and "leave with next door"
  are operational, and blanking them would strip instructions off live tickets.
  A merchant answering an erasure request should review notes on recent orders by
  hand if the customer's name appears in one.
- backups. Copies under `backups/` still contain the erased data until they age
  out of the 14-day retention. This is normal and defensible, but say so if asked.

`online_payload` is the one most easily forgotten: it holds the marketplace's
whole JSON, name and address included, long after the columns beside it were
tidied. It is cleared.

### Why orders survive an erasure

HMRC requires the records behind a VAT return to be kept for six years. GDPR's
right to erasure does not override a legal obligation to retain. The two are
reconciled by erasing the **identity** and keeping the **transaction** — the sale
stays on file, with `[erased]` where the name was.

## Accountability

Both erasure paths write an audit entry and a log line, and both record **counts
only** — never the name that was just removed. An audit trail that repeated the
data would reinstate exactly what it was recording the removal of.

## Security

- The database lives under `%PROGRAMDATA%\RingOrder\EPOS`, on the merchant's own
  machine. There is no central store of customer data and no telemetry.
- Staff PINs are PBKDF2 with a per-user salt; nothing else is hashed, because
  nothing else needs to be verified rather than read.
- Access to the phone book and to erasure is gated on `Permission.EditSettings`.
- API keys live in the till's database and in `secrets.json`, never in the shop
  bundle, which gets emailed and copied around.
- Backups are local, dated, and pruned at 14 days.

**Not done yet, and known:** the database is not encrypted at rest, so a merchant
with a laptop that leaves the premises is relying on Windows account security. If
a shop needs more, full-disk encryption on their machine is the answer today. See
[DEPLOYMENT.md](DEPLOYMENT.md) for what is decided versus proposed.
