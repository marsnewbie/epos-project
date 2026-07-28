# Lessons from cloudpos-edge / PosNext (avoid repeats)

Earlier experiments lived under `C:\Projects\cloudpos-edge` (React SPA + Node local-edge + PosNext .NET). We borrow ideas, not the dual-process shape.

## Avoid

1. Browser UI + separate localhost agent as the long-term architecture.  
2. Marking print jobs `printed` when only console/log rendered — **paper is the truth**.  
3. Non-idempotent print ack → infinite reprints.  
4. Documented offline queue that was never implemented.  
5. Cash drawer that only works over network `:9100` ESC/POS.  
6. “Simulated Stripe” looking like real terminal integration.  
7. Two competing navigation paradigms (Western session vs Chinese grid) without choosing one.

## Keep

1. PrintJob channels: kitchen vs front.  
2. Draft → send/pay → close mental model.  
3. Strong modifier validation.  
4. Quick kitchen note chips (no onion, extra spicy…).  
5. Customer multi-address book for delivery.  
6. Explicit UTF-8 / Chinese receipt encoding care on Windows.

## This project’s correction

**.NET + Avalonia single app** + HAL + SQLite + website online poller.
