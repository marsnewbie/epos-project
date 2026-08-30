# ringorder-pos-cloud

The entitlement authority. It answers one question — *what has this shop
bought?* — and it is designed so that a till never has to wait for the answer.

Read [docs/CLOUD.md](../docs/CLOUD.md) first. It holds the decisions; this file
holds how to run the thing.

## Why it lives in this repository

Because it and the till **co-evolve one contract they both own**. A change to the
token payload or the sync envelope has to land on both sides at once, which here
is a single commit and across two repositories is two pull requests and a window
where the halves disagree.

The AI phone project chose separately, for reasons that do not apply: it consumes
an API it does not control and deliberately does not change.

Railway's **Root Directory** is set to `cloud`, and its watch paths keep C#
commits from redeploying the service.

## Running it

```bash
cd cloud
npm install
npm test          # 41 tests, no database and no network needed
npm run typecheck
npm start
```

Node 24 runs TypeScript directly, so there is no build step. That constrains the
source to **erasable syntax only** — no `enum`, no `namespace`, no constructor
parameter properties. `tsconfig.json` sets `erasableSyntaxOnly` so the compiler
says so rather than the runtime.

One runtime dependency, `pg`. The phone project has none because Supabase speaks
HTTP; Railway's Postgres speaks its own wire protocol and there is no honest way
around a driver.

## Environment

| Variable | |
|---|---|
| `DATABASE_URL` | Railway provides it. Required |
| `SIGNING_KEY` | The private key, PEM or base64 of one. Required |
| `PORT` | Railway provides it. Defaults to 8080 |
| `MIN_CLIENT_VERSION` | Optional, and **absent by default on purpose** — a floor set casually cuts off whoever has not updated |
| `ADMIN_TOKEN` | Guards the admin endpoint. **Absent closes it** rather than opening it, so a deployment that forgot one is safe by accident |

### The key nobody can lose

```bash
node tools/keygen.mjs
```

The private half goes into `SIGNING_KEY` **and into an offline backup somewhere
that is not the hosting platform.** If it is lost, no token can be signed and
every till on the estate degrades within thirty days with no remedy. It is the
one failure that reaches every customer at once.

The public half goes into `EntitlementKeys.Production` in the till — current key
first, and ship two entries once you have them. Rotating a signing key needs a
period where both are accepted, and the day you need that is the day you cannot
update everyone first.

The key in `fixtures/entitlement` is for development only and must never be used
here. A test holds it out of the till's trusted list.

## The endpoints

```
POST /v1/activate      one-time; a typed code  → device secret + first token
POST /v1/sync          recurring; device secret → token
POST /v1/admin/shop    bearer token; creates a shop and mints its code
GET  /v1/admin/shops   bearer token; the estate, and what build each till is on
GET  /admin            the page an operator uses
GET  /healthz          includes a database ping
```

`sync` rather than `entitlement` because it is the one call a till makes on a
schedule, and order ingest and the change log will arrive in that same answer as
additional fields. The till ignores fields it does not recognise, so they can be
added without breaking anything already installed.

## Rules this service keeps

**A known device is never refused for commercial reasons.** A shop that stops
paying has its row changed and is told what it now has; the till degrades to
exactly what we decided it keeps. Refusing outright would surrender that control
and land the change thirty days later on a day nobody chose. The one exception is
a device whose shop has been deleted, which is a deliberate act on our side.

**Activation is idempotent.** A till whose connection dropped between our answer
and its write holds a key and no secret; its only way out is to ask again.
Refusing would strand that machine for good.

**No orders, no customers, no money.** The absence of those columns is the
enforcement. Adding one is a decision that belongs in `docs/CLOUD.md` with a
reason.

## Database

**Migrations run at startup, before the port opens.** A deploy that cannot
migrate fails as a deploy rather than as a 500 an hour later that nobody
connects to the schema. The till has worked this way since its first release,
for the same reason.

`migrations/*.sql` run in filename order, one transaction each, recorded in
`schema_migrations`. Concurrent instances during a rolling deploy are handled by
a `pg_advisory_lock` — the second waits and then finds nothing to do.

This was manual for exactly one day, and the very first setup created two tables
with one column each through a button in a database console. `CREATE TABLE IF
NOT EXISTS` then does nothing to fix them, silently, which is the worst shape a
failure can take.

> **Adding a migration:** new file, next number, never edit one that has shipped.
> `IF NOT EXISTS` guards a create; it does **not** reconcile a table that already
> exists with the wrong columns.

## Adding a shop

Open **`/admin`**, paste the `ADMIN_TOKEN` once, and fill in the name. The page
shows the code and lists the estate — how many tills each shop has, what build
they are on, and when each was last heard from.

The page is served without a gate on purpose. It holds no secret: the token is
typed into it and stays in that browser, and every call it makes is authorised
exactly as `curl` would be. Gating the HTML too would only mean two places to get
authorisation wrong.

There is a command for the same thing, for scripting:

```bash
ADMIN_TOKEN=... node tools/new-shop.mjs <shop-id> [pos|print] [terminals]
```

Both print an eight-character code. Somebody types it on the till at
**Settings → Cloud → Connect**, and that is the whole of onboarding.

**The code identifies the shop.** The till is told nothing about which shop it
belongs to — that is what removed the per-merchant file edit, and it means the
shop id is ours to choose rather than something that has to match a bundle.

Codes expire after seven days and are stored only as a hash. A short code is a
weaker secret than a long one, and an expiry is the honest way to pay for that
rather than more characters nobody can type. Losing one costs another run of this
command.

Re-running it on an existing shop replaces the code and the old one stops working
immediately, which is the recovery path.

## Not built yet

Order ingest, the change log, and the back office. The protocol is shaped for
them; none of them exist. **The contract is still free to change** — no till is
installed anywhere — and it stops being free at the first real merchant
installation. See the note in docs/CLOUD.md and update it on the day.
