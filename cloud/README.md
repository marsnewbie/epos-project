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
npm test          # 34 tests, no database and no network needed
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
POST /v1/activate    one-time; activation key  → device secret + first token
POST /v1/sync        recurring; device secret  → token
GET  /healthz        includes a database ping
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

`migrations/001_initial.sql`, applied by hand for now — two tables and one
service. When that stops being comfortable, a migration runner goes here and this
line changes.

## Not built yet

Order ingest, the change log, and the back office. The protocol is shaped for
them; none of them exist. **The contract is still free to change** — no till is
installed anywhere — and it stops being free at the first real merchant
installation. See the note in docs/CLOUD.md and update it on the day.
