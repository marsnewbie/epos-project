// Generates a production signing key pair.
//
//   node cloud/tools/keygen.mjs
//
// Prints the private key and the public key and writes nothing. Piping it into
// a file in this repository is how a production key ends up in version control,
// so it is deliberately awkward to do by accident.
//
// The private half goes into the service's SIGNING_KEY environment variable
// AND into an offline backup somewhere else. If it is lost, no token can be
// signed and every till on the estate degrades within thirty days with no
// remedy — it is the one failure that reaches every customer at once.
//
// The public half goes into EntitlementKeys.Production in the till, current key
// first. Ship two entries once you have them: rotating a signing key is
// impossible without a period where both are accepted, and the day you need
// that is the day you cannot update everyone first.

import { generateKeyPairSync } from "node:crypto";

const { privateKey, publicKey } = generateKeyPairSync("ec", {
  namedCurve: "prime256v1",
  privateKeyEncoding: { type: "pkcs8", format: "pem" },
  publicKeyEncoding: { type: "spki", format: "pem" },
});

const spkiBase64 = publicKey
  .split("\n")
  .filter((line) => line && !line.startsWith("-----"))
  .join("");

console.log("── SIGNING_KEY (private — the service environment, and an offline backup) ──\n");
console.log(privateKey);
console.log("── EntitlementKeys.Production (public — paste into the till) ──\n");
console.log(spkiBase64);
console.log();
console.log("Back the private key up somewhere outside the hosting platform before you use it.");
