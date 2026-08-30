// Adds a shop and prints the code somebody types on the till.
//
//   ADMIN_TOKEN=... node cloud/tools/new-shop.mjs <shop-id> [pos|print] [terminals]
//
// Talks to the running service. It used to print SQL for a person to paste into
// a database console — which is how the first shop was added, and how two tables
// came to be created with one column each.
//
// Re-running it on an existing shop replaces the code. That is the recovery path
// for a lost one, and the old code stops working immediately.

const [shopId, edition = "pos", terminals = "1"] = process.argv.slice(2);

const BASE = (process.env.CLOUD_URL ?? "https://epos-project-production.up.railway.app").replace(/\/+$/, "");
const TOKEN = process.env.ADMIN_TOKEN;

if (!shopId) {
  console.error("usage: ADMIN_TOKEN=... node cloud/tools/new-shop.mjs <shop-id> [pos|print] [terminals]");
  console.error("       the shop id is yours to choose; the till never has to know it");
  process.exit(1);
}

if (!TOKEN) {
  console.error("ADMIN_TOKEN is not set. It is the ADMIN_TOKEN variable on the service.");
  process.exit(1);
}

const response = await fetch(`${BASE}/v1/admin/shop`, {
  method: "POST",
  headers: {
    "content-type": "application/json",
    authorization: `Bearer ${TOKEN}`,
  },
  body: JSON.stringify({ shopId, edition, terminals: Number(terminals) }),
});

const body = await response.json().catch(() => ({}));

if (!response.ok) {
  console.error(`\n  ✗ ${response.status} — ${body.error ?? "no reason given"}`);
  if (response.status === 404) {
    console.error("    The service has no ADMIN_TOKEN set, so the admin endpoint is closed.");
  }
  process.exit(1);
}

const expires = new Date(body.expiresAt).toLocaleDateString("en-GB", {
  day: "numeric",
  month: "long",
  year: "numeric",
});

console.log(`
  ${body.shopId} — ${body.edition === "print" ? "web-order printer" : "full till"}, ${body.terminals} terminal(s)

      ┌──────────────────────┐
      │      ${body.activationCode}      │
      └──────────────────────┘

  Type it on the till: Settings → Cloud → Connect.
  Upper or lower case, with or without the dash.

  Valid until ${expires}. Shown once — only a hash is stored, so it cannot be
  read back. Losing it costs another run of this command and nothing else.
`);
