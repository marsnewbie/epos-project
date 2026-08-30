# Entitlement contract fixtures

Tokens **signed by Node and verified by C#**, because the service and the till
are different runtimes and the failure worth guarding against is the one where
each is perfectly self-consistent and they disagree with each other.

Node signs ECDSA in DER by default. .NET verifies P1363 by default. Two correct
implementations that never interoperate until somebody pins the encoding — a
round-trip test written in one language passes happily and proves nothing.

```bash
node fixtures/entitlement/make-fixtures.mjs
```

Read by `EntitlementTokenTests` and `EntitlementServiceTests`, and by the cloud
service's own tests once it exists. Both sides read the same bytes; that is the
whole point.

## `dev-private.pem` is in version control on purpose

It is a **development and test key**. It signs the fixtures and nothing else.

It is deliberately **absent from `EntitlementKeys.Production`**, and
`EntitlementTokenTests.The_development_key_is_never_trusted_by_a_shipped_build`
holds it out. A build that trusted this key would accept an entitlement anybody
with a copy of this repository could mint.

**The production signing key is generated separately and never enters this
repository.** It lives in the service's environment and in an offline backup —
see [DEPLOYMENT.md](../../docs/DEPLOYMENT.md) and
[CLOUD.md](../../docs/CLOUD.md).

## The cases

| Fixture | What it holds |
|---|---|
| `current` | The ordinary answer |
| `print-only` | A restricted shop: edition, seat count, populated allow-list |
| `expired` | Correctly signed, a month past expiry. **The till must trade on this** |
| `other-device` | Correctly signed, issued to a different machine |
| `future-version` | A payload version this build does not know |
| `unknown-fields` | Carries fields the till has never heard of, and must still load |
| `tampered` | Signed, then the payload edited afterwards |
