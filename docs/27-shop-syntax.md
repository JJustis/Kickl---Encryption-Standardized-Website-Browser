# 27 — Selling for Bitcoin: the `<shop>` syntax

> Scope: the slice as implemented (`Kickl.Core/Markup/Kml.cs`, `Bitcoin.cs`, `BrowserWindow.Shop.cs`). A page can list products, a price in
> satoshis for each, and the Bitcoin address that gets paid. There is a working example at `jttp://btcshop.com`.

## 1. What it is, and what it is not

A shop in Kickl is **markup, not a payment system.** The page says what is for sale and where to send the money. The browser draws it,
works out the exact amount, and hands the buyer a payment link. Then it stops:

- It **never sends a payment** and **cannot see one.** There is no wallet in the browser.
- The **seller confirms** that the money arrived and delivers the goods: by post, by mail, or by sending the access key to a sealed file
  that is already inside the page.
- **Bitcoin payments cannot be reversed.** The page says so next to every price.

Because the shop is part of a sealed, hash-checked page, nobody between the seller and the buyer can change the address or a price without the
page failing its integrity check.

## 2. The syntax

```xml
<shop name="My shop" address="bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4">
  <product id="sticker" name="Sticker pack" price="1500">
    Three vinyl stickers, shipped anywhere.
  </product>
  <product id="guide" name="Field guide" price="50000" blob="guide" access="protected">
    A PDF, sealed in this page. You send the buyer its access key.
  </product>
</shop>
```

| Element / attribute | Rule |
|---|---|
| `shop` `address` | **Required.** A complete Bitcoin address: legacy (`1…`, `3…`), Segwit (`bc1q…`), or Taproot (`bc1p…`), on mainnet or testnet (`tb1…`, `m…`, `n…`, `2…`). |
| `shop` `name` | Optional, up to 80 characters. |
| `product` `id` | **Required.** 1–32 lowercase letters, digits or hyphens; unique in the shop. |
| `product` `name` | **Required.** 1–80 characters. |
| `product` `price` | **Required.** A whole number of **satoshis**, 1 to 2,100,000,000,000,000. No decimals, no signs. 1 BTC = 100,000,000 sats. |
| `product` text | The description, up to 500 characters. Text only. |
| `product` `blob` | Optional. The id of a file in the site's `blobs/` folder, delivered sealed with the page. |
| `product` `access` | `public` or `protected`. A **protected** file only opens with the access key the seller gives out. |
| `product` `mime` | Optional. The file's type, if the extension does not say. |
| Limits | At most 24 products per shop. Only `product` may go inside a `shop`. No scripts, styles or event attributes, as everywhere in KML. |

### Why satoshis

Prices are integers, so there is never a rounding error and no "0.1 + 0.2" surprise. The browser shows both forms for every product:
`50,000 sats` and `0.00050000 BTC`, and builds a [BIP 21](https://github.com/bitcoin/bips/blob/master/bip-0021.mediawiki) payment link,
`bitcoin:ADDRESS?amount=0.0005&label=…`, which wallets open or scan.

## 3. The address is checked before you can publish

A mistyped address sends money to nobody, forever. So the parser checks the address completely, **checksum included**, and refuses the
page if it does not match:

- legacy Base58Check (`1…`, `3…`; testnet `m…`, `n…`, `2…`);
- Segwit Bech32 for version 0 (`bc1q…`, 20 or 32 byte programs) and Bech32m for Taproot (`bc1p…`, 32 byte programs), with the case rules of
  BIPs 173 and 350.

Any single-character typo is caught (the tests try every position). This only proves the address is **well formed**; nothing can prove whose
it is. Check it in your wallet before you publish.

## 4. What the buyer sees

Each product is a card with its price. **BUY** opens a payment panel:

1. the exact amount in BTC and satoshis, and the seller's address;
2. **COPY ADDRESS**, **COPY AMOUNT** and **COPY PAYMENT LINK** (the link goes into a wallet);
3. a reminder to check the address in the wallet, and to send a small test payment first for a large amount;
4. for a product with a file: that the sealed file is the block right under the shop and that the seller sends the access key after payment.

Above the cards, a line says who is selling. A name sealed by the Kickl CA shows **VERIFIED OWNER**, which means the person who holds the name,
not that the goods are real. An unsealed or unclaimed name says plainly that the seller is unverified: **anyone can publish a page with any
address.** A **testnet** address shows a **TESTNET · NO REAL MONEY** badge.

## 5. Publishing a shop

**In the browser.** Create a name, open its editor, and press **SHOP · SELL FOR BITCOIN** above the page box. You get a working shop built on a
testnet address; change the address, products and prices. The preview underneath is drawn by the real parser, so a mistyped address or a bad
price is shown there, in red, before you publish. (A product with a file, `blob=`, needs a site folder; the editor tells you so.)

**From a site folder** (for a shop that ships sealed files):

```
sites/myshop.com/
  site.kml            the page, with a <shop>
  blobs/guide.pdf     the file; its id is the name without the extension
```

```
kickl publish --site sites/myshop.com --host myshop.com --pin YOUR-PIN --access-key SOMETHING-LONG
```

Protected files are sealed under a key made from the access key. Without `--access-key` one is generated and printed once; it is **not stored**,
so write it down. Give each buyer the key after their payment has arrived. The key opens every protected file on the page.

## 6. The example: `jttp://btcshop.com`

`sites/btcshop.com` is modelled on the BTC shop at `secupgrade.com/btcshop` (same kinds of goods, same prices: 2,418 sats for PathForge Pro,
12,090 for Surviving Day, 3,000 for an ad upload, 10,000 to claim a name). It pays a **testnet** address on purpose, so trying it can never lose
real money. PathForge Pro carries a sealed text file; the demo access key is `demo-paid`.

The demo profile publishes it automatically (`--demo`), so `jttp://btcshop.com` opens straight away.

## 7. What this does not do

- **No payment detection.** The real BTC shop watches a block explorer for the payment. A page cannot (it has no network), so the seller confirms
  by hand, or uses a service of their own.
- **No cart, stock levels or fiat conversion.** A price is a fixed number of satoshis; if Bitcoin's price moves, the seller changes the page.
- **No QR code yet.** Copy the payment link, or the address and amount.
- **No proof of identity.** See §4. For a claimed, CA-sealed name the badge shows who holds the name; reviews and refunds are between people.
- **A product's access key is shared by everything protected on the page.** Use separate pages for goods that must not share a key.
