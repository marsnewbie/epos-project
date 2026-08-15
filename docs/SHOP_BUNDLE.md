# The shop bundle, and putting a shop live

One signed binary goes to every merchant. A `shop.ringpos.json` is the entire
difference between two of them.

## Why a file and not a download

The till does not fetch its setup from anywhere. Most merchants who buy the till
have no website from us, and the ones who do usually want the till working
before the site exists — a new shop needs to take orders on opening day, and the
website can follow. So the bundle is built by us, packaged with the installer,
and imported on first run.

After import **the till owns its data**. The bundle is a seed, not a runtime
dependency; every field is editable in Settings, and a shop is never waiting on
us to change a price.

## Shape

```jsonc
{
  "schemaVersion": 1,
  "profileVersion": "2026-08-14.1",   // ours, for support: which build is installed

  "shop":     { "slug", "name", "address", "postcode", "phone", "email",
                "vatNumber", "allergyNotice" },
  "locale":   { "currency", "uiLanguage", "kitchenLanguage" },

  "tax":      { "pricesIncludeTax": true,
                "classes": [ { "id", "name", "rateBasisPoints" } ],
                "defaultClassId" },

  "serviceTypes": [ { "id": "collection|delivery|eat-in", "name", "isDefault" } ],

  "menu": {
    "categories":   [ { "id", "name", "translation", "sortOrder", "isVisible",
                        "printClass", "taxClassId" } ],
    "optionGroups": [ { "id", "name", "type": "single|multi", "required",
                        "minSelections", "maxSelections",
                        "choices": [ { "id", "label", "translation",
                                       "priceDeltaPence", "isDefault", "isAvailable" } ] } ],
    "items":        [ { "id", "categoryId", "menuNumber", "name", "translation",
                        "pricePence", "taxClassId", "printClass", "isAvailable",
                        "sortOrder",
                        "optionGroups": [ { "groupId", "sortOrder",
                                            "showWhen": { "groupId", "choiceIds" } } ] } ]
  },

  "quickNotes": [ { "en", "zh" } ],
  "delivery":   { "defaultFeePence", "zones": [ { "prefix", "feePence", "minimumOrderPence" } ] },
  "printing":   { "devices": [ … ], "routes": [ … ] },
  "channels":   { "counter", "phone", "web", "platform" },
  "staff":      [ { "name", "role": "cashier|supervisor|manager", "pin", "mustChangePin" } ],
  "receipt":    { "headerLines": [], "footerLines": [] }
}
```

**Money is always integer pence.** `620`, never `6.20`. Tax is basis points:
`2000` is 20%, and never 0.19999999.

**Rates are per class, prices are one list.** UK hot takeaway food is standard
rated and cold food is not, which changes what the receipt declares — not what
the customer is charged. There is no second price list anywhere in this product.

## Credentials

Print credentials for a shop's website go in `secrets.json` **beside** the bundle,
never inside it. Bundles are diffed, reviewed and copied around; secrets are not.
The importer picks up a sibling `secrets.json` when present, and its absence is
normal — most shops have none.

A postcode-lookup account belongs there too, and for a sharper reason: the key is
billable, so a bundle that carried it would let anyone it was forwarded to spend
the merchant's credits.

```json
{
  "shopSlug": "demo",
  "addressLookup": { "provider": "getaddress", "apiKey": "…" }
}
```

`provider` is one of `none`, `postcodesio`, `getaddress`, `idealpostcodes`.
Omitting the block leaves lookup switched off, which is the right default — see
[ARCHITECTURE.md](ARCHITECTURE.md) for why there is no free option that returns
house numbers.

## Putting a shop live

The merchant sends whatever they have. It is never the same twice.

1. **Keep the source.** `ringorder-epos-shops/<slug>/source/` — the PDF, the
   photographs, the spreadsheet. Enter what is legible, take the closest sensible
   reading of what is not, and leave genuinely absent things unset. An owner
   reviewing their own menu spots a gap faster than they can read a list about it.

2. **Build the bundle** into `ringorder-epos-shops/<slug>/shop.ringpos.json`.
   One generic path for every shop. **Never a per-shop script** — the second copy
   is the one nobody keeps in step, and by the third shop none of them agree.

3. **Check it.** Duplicate menu numbers, prices that are an order of magnitude
   out, option groups nothing references, a `showWhen` pointing at a choice that
   does not exist, a tax class that is not declared. The importer reports all of
   these as warnings; a clean import is the bar.

4. **Look at a ticket** before the shop does. The kitchen ticket and the receipt
   are what the merchant judges us on.

5. **Package and install.** Signed installer plus the bundle; first run imports
   it. What is left is physical: which printer is the kitchen, a test print, the
   drawer, the caller ID box.

6. **Change the PINs.** The bundle seeds one manager account with
   `mustChangePin`, and the staff list in Settings says so until it is done.

## Menu changes after go-live

Small changes — a price, a dish, sold out — are made in Settings, by us over
remote support or by the merchant. That is the normal path, and it does not
involve a file.

A re-import replaces the whole catalogue and is for a menu rebuild. It leaves
orders, customers and shifts alone; a menu update mid-week must not erase the
week. Anything edited in Settings since the last import is lost, so the bundle
in `ringorder-epos-shops/` must be updated to match rather than allowed to drift
into fiction.

## Back up the shops folder

`ringorder-epos-shops/` is git-ignored on purpose — hundreds of merchant menus
have no business in the product's history, and credentials have no business in
git at all. But ignored means unversioned: losing a bundle means re-entering a
merchant's menu by hand. Make that folder its own private repository, or make
sure whatever backs up the machine includes it.
