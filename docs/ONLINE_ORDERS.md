# Online orders (website integration)

## Architecture

One shared website print queue (`claimNextPrintOrder`). Two first-class adapters:

| Device | Format | Endpoints |
|--------|--------|-----------|
| Handheld GcAnyOrder | Goodcom text | `/api/print/gcanyorder/getorder` + printed/callback |
| Windows Magic Wok EPOS | JSON | `/api/print/epos/next` + `/api/print/epos/ack` |

Same Admin → Print credentials (`a` / `u` / `p`). Merchant turns on **one** device at a time.

Guest checkout is unchanged. Website docs: `docs/PRINT_DEVICES.md` in magicwoksite.

## Default EPOS Settings (Magic Wok)

| Setting | Initial value |
|---------|----------------|
| Shop base URL | `https://magicwoksite.vercel.app` |
| Next (JSON) | `https://magicwoksite.vercel.app/api/print/epos/next` |
| Ack | `https://magicwoksite.vercel.app/api/print/epos/ack` |

Copy username/password/RES ID from Admin → Print. Never commit secrets.

When the public domain changes: Settings → Online → update base URL (or paste the Admin EPOS URLs).

## EPOS behaviour

1. Poll JSON next (204 = empty).
2. Map DTO → `PosOrder` (full kitchen fields: requested-for, payment, bilingual lines, fees…).
3. Kitchen print via ESC/POS to GlPrinter80 (EPOS-owned layout; information-equivalent to GcAnyOrder).
4. Ack printed on the website.

Fallback: if Order Server URL still points at `gcanyorder/getorder`, the poller accepts Goodcom text.

## Kitchen ticket design

- Translate website payload → Domain completely.
- Present with professional ESC/POS (not Goodcom XML `\1\2\3`).
- Encoding: Settings `PrintEncoding` (`gb18030` default, or `utf8`) — use test page to verify Chinese on GlPrinter80.
