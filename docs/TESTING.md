# Testing

```bash
dotnet build RingOrder.Epos.sln
dotnet test RingOrder.Epos.sln
dotnet run --project src/RingOrder.Epos
```

Automated tests cover the arithmetic and the data. They cannot tell you whether
a shift can be closed or a ticket can be paid, so the manual passes below are
part of finishing a change, not an extra.

## What the tests cover

| Area | Why it is tested |
|---|---|
| Money conversion | A till whose day does not add up is not a till |
| Option engine | Required, optional, pick-two, conditional reveal, meal deals — using the fixtures that used to be sample dishes on the demo menu |
| Bundle import | The demo shop's menu must survive import exactly: counts, prices to the penny, every group reference, every conditional |
| Tender and shift arithmetic | Partial payment, split tender, expected cash, variance, sequential shift numbers |
| Permissions and PIN hashing | Who may do what, and that a PIN is not recoverable from what is stored |
| Migrations | A database written by an older release still opens, still has its orders, and gains its new columns |
| Print routing | Which device a document goes to, and that a dish inheriting its category's station still reaches the kitchen |
| Backups | That the nightly copy is taken, is openable, and that old ones are pruned |
| VAT | Prices include tax, so the arithmetic runs backwards; net plus VAT reconstructs the gross for every penny from 1p to £50 |
| Postcode lookup | That one house typed three ways is paid for once, that rubbish never reaches a paid provider, and that a timeout is not cached |
| Address book | That one door is one row however it is spelled, that two customers can share it, that one customer can hold several, and that the old JSON blob moves across without loss |
| Privacy | That erasing a customer removes the person and keeps the sale — including the web-order payload, which is the easiest thing to leave behind |

The migration test seeds the old database with raw SQL matching that version's
schema. Using today's repository would test nothing, because it already knows
about columns the old release never had.

The lookup tests never touch the network. Provider responses are parsed from
recorded JSON, and the cache and fallback behaviour run against a counting fake —
so "how many times did the shop get charged" is an assertion rather than a hope.

## Manual pass — the money

Do this one after any change to payment, shifts or printing.

1. Sign in. Open a shift with a float of £100.
2. Ring two dishes. Send to the kitchen — **the ticket stays on screen**.
3. Add another dish. The button offers to send only the new one, and the ticket
   prints marked as an addition.
4. Take £5 cash against a £20 ticket. Ticket stays open, balance shows £15, no
   final receipt.
5. Take the balance on card. Now it settles, and the receipt prints.
6. Ring another ticket, £12.40, tender £20. Change shows £7.60, large, and stays
   until acknowledged.
7. Apply a discount — try `5` and `10%`, and check the reason is demanded.
8. Close the shift. It asks what you counted **before** it shows what it expected.
   Check the variance is what you would work out on paper.

## Manual pass — who did it

1. Sign in as a cashier. Try to void an order.
2. A supervisor PIN is asked for; the ticket is still there afterwards.
3. Sign in as a manager. Void an order — no second challenge.
4. Settings → Staff: add someone, reuse an existing PIN (refused), switch the
   last manager off (refused).

## Manual pass — the menu

1. Settings → Menu: change a shared option group from one dish. The status line
   names the other dishes it changed.
2. Sell: a dish with a conditional group only reveals it for the triggering
   choice, and does not demand it otherwise.
3. Mark a dish sold out. It greys **in place** on the grid rather than
   disappearing.
4. Paging: page 2 of a category is the same page 2 after switching away and back.

## Manual pass — provisioning

Worth doing before any release, because it is what every new merchant does first.

1. Move `%PROGRAMDATA%\RingOrder\EPOS\data.sqlite` aside.
2. Start the app. It imports the bundle from `profile/` and logs what it found.
3. Check the counts against the bundle, and that there were no warnings.
4. With no bundle either, the till should say it needs setting up — not fail, and
   certainly not show another shop's menu.

## Printing

Needs the hardware, so it is checked when a printer is attached. Kitchen ticket
with Chinese and English, a receipt, the drawer, and a test page that shows the
paper width. **Paper out of the machine is the pass; a status column is not.**
