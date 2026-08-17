# Interface

The people using this did not choose it, are on their feet, and are ten hours
into a shift. They will use the till they can work fastest on, and if that is
not ours they keep the one they had.

## The rules

**Colours and sizes come from `Styles/Tokens.axaml`.** No hex literals in views.
When every screen picked its own greys, nothing quite matched anything.

**Nothing a finger presses is under 44px. Anything that takes money is 64.**
Body text is 16px. The screen is often a 15" 1366×768 resistive panel, the
lighting is bad, and the hands are greasy.

**Colour means one thing and only one.** Green is cash, blue is card, amber is
held or owing, red is void or broken, accent orange is *selected* or *the next
step*. A colour used decoratively costs its meaning everywhere else.

**Position is memory.** Dishes are a fixed grid with paging, never a flow layout
in a scroll view. Staff learn where a dish is and stop reading; a flow layout
moves every tile after a rename, and a scroll position is wherever the last
person left it. Page 2 of Chicken is always page 2 of Chicken.

**Sold out greys out in place.** A dish that vanishes reads as "I am in the wrong
category" and sends someone hunting through the menu.

**Status that fails silently is shown permanently.** Printers and the web feed
have lights in the top bar. Finding out the kitchen printer is unplugged when
the ticket does not arrive is a service ruined; seeing it at 4pm is a two-minute
fix.

**A failure that costs money blocks; a fact informs.** Print failures need a
dialog. "Signed in as Wei" is a status line.

**Every screen says who is signed in and which shift the money lands in.**

## Naming

The words on the buttons are the trade's words, not ours.

| We show | Not | Why |
|---|---|---|
| **Till** | Sell, Point of Sale | What the trade calls the screen you ring a sale on, and it does not collide with Orders |
| **Orders** | Transactions, History | |
| **Collection** | Pickup, Takeaway | UK usage: phoned ahead, collecting later |
| **Waiting** | Walk-in | Standing at the counter now. Different urgency in the kitchen, and staff already use the word |
| **Delivery** | | |
| **Eat in** | Dine-in, Table service | |
| **Web orders** | Online | "Online" also means the internet being up |
| **Discount** | Promotion, Adjustment | |
| **Void** | Cancel, Delete | Cancel is what a dialog does; void is what happens to a sale |

## Flows worth stating

**Sign in.** PIN on a keypad, no keyboard. A counter has none, and an on-screen
QWERTY is a PIN read over the customer's shoulder.

**Supervisor override.** When a cashier hits something they may not do, the
supervisor types their PIN into the same screen and the ticket survives.
Requiring a sign-out would lose the half-built order, and staff would work around
it by sharing a login.

**Opening a ticket.** Service type is always visible and one tap to change,
defaulting to the shop's commonest. A modal that demands the type before anything
can be typed slows down the case that happens most.

**Adding dishes.** Tap a tile, or key the number — `88`, or `3x88` for three.
Experienced staff work by number and barely look at the tiles. Required options
open immediately; a dish with none goes straight onto the ticket.

**Sending to the kitchen.** The ticket stays on screen. Added lines print alone,
marked as additions, because a kitchen re-reading a whole ticket to find one new
dish makes mistakes.

**Taking money.** Partial payment leaves the ticket open with the balance
visible. Change is shown large and stays until acknowledged — a change figure
that clears itself is how staff hand over the wrong money.

**Closing a shift.** The counted amount is entered *before* the expected figure
is shown. A till that volunteers the answer first is not counting the drawer, it
is confirming it.

## Language

Two independent things, and conflating them was a bug once:

- **Interface language** switches the chrome — nav, buttons, dialogs. One
  language at a time; never English and Chinese on the same button.
- **Menu language** does not move. The counter shows the English dish name with
  the kitchen's Chinese underneath, and the kitchen ticket prints both, whatever
  the interface is set to. The kitchen and the customer read different languages
  and always will.

**Taking payment.** The whole screen. Owed on the left in the largest type on
the till, keypad on the right with the note buttons (£5/£10/£20/£50) down the
side nearest the confirm button — the commonest case by far is a customer
handing over a note. Both confirm buttons name their amount: "Exact £10.60",
"Card £10.60". A button that says only "Card" makes the cashier check the figure
somewhere else, and the once they do not is the one that goes wrong.

**A new web order** gets a band across the top of the screen that stays until
someone opens Orders. Nobody is watching the screen when it arrives, so a
notification that fades has not notified anyone.

**Shift readings.** An X looks at the open shift and changes nothing; a Z is the
closing account and prints itself when the shift is closed. Both live in
Settings → Shift, where closed shifts are listed with their variance so the one
night that did not balance is findable without opening anything.

The reading is per **shift**, never per day. A shop trading past midnight would
otherwise have every figure split across two dates, and one that opened twice in
a day would have both sessions added together — and the drawer is counted per
shift.

## Still to build

- Keyboard shortcuts for tills with a numeric keypad
- A Delivery screen for driver dispatch
- The print-only edition's tray shell — see [DEPLOYMENT.md](DEPLOYMENT.md)
