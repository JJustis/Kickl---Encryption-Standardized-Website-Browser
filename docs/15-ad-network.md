# 15 — The Nickles Ad Network

Ads are allowed **only** through this system. They appear on Kickl-owned surfaces and in ad spaces
that a webmaster explicitly opts into. No third-party ad script, pixel, SDK, or tracking network
exists anywhere in the product. The publisher's `nickles.js` is a signed placement manifest, not an
executed script: sealed pages still have no script engine.

## 1. Principles

1. **No tracking.** No impressions, clicks, cookies, fingerprints, geo, or behavioural targeting are
   measured or transmitted. Rotation is time-based and identical for everyone.
2. **No content takeover.** Ads never appear over a website that did not opt in, in mail, in chat, in
   the vault, or as an uninspected overlay on a sealed page. A publisher slot exists only where the
   webmaster pasted the NCKL snippet.
3. **Always labelled.** The word `AD` is visible at all times, in the theme's mono micro-label.
4. **One slot per page in 1.0.** A site uses `site.inline`; the home uses
   `home.footer`. Adding another slot requires a design review and a new ADR.
5. **Dismissible on owned surfaces.** The home slot collapses to a 1 px strip with the `AD` label; the
   preference is local. A publisher slot belongs to the page's layout and collapses with the page.
6. **Payment is the only ranking input.** More donation buys a larger share of rotation, never data.
7. **Site-bound.** A publisher token is bound to one jttp host and one space. Copying a snippet to
   another host fails closed at publish and at render.

## 2. The slot

```
placement id: home.footer
size:         full-width browser-pane footer bar, ~64 px tall (creative is inline text/art)
position:     below the home content, above the global status bar — part of layout, never an overlay
responsive:   spans the available page width; on narrow or stacked layouts it stays a full-width bar
animation:    one 200 ms opacity fade on change; no video, no sound, no parallax, no autoplay
```

- The home slot lives in the **home** surface only. Publisher slots are a separate opt-in mechanism
  described in §2a; neither is injected into a page that did not ask for it.
- In the browser slice, the home creative is selected from the local catalog by the global
  `floor(unix/600)` bucket and refreshes at the next bucket boundary. The collapse choice is stored
  locally in the profile; the strip keeps a visible `AD` label.
- It never overlaps the dock (bottom-right) or the panic control.
- Keyboard focus reaches the creative; `Enter` opens the destination in the clearnet modal with
  `rel="noreferrer noopener"` and a visible "leaving Kickl" transition.
- Empty state: a 1 px `AD` strip. No house ads disguised as content, no " sponsored content" cards in
  the arcade feed.

## 2a. The publisher slot (`site.inline`)

A webmaster can opt a sealed page into NCKL. The flow is deliberately explicit:

```
1. Open jttp://secupgrade.com/adnetworking/publisher.
2. Enter the jttp host you control and the ad space (site.inline in the slice).
3. Mint the placement. You get a publisher id, a site-bound token, nickles.js, and a snippet.
4. Paste the snippet into the page's inline HTML, with the ad-space div where the ad should appear.
5. Publish. The composer verifies the signature and stores one <ad publisher site space token> block.
```

The generated snippet looks like this:

```html
<div data-nickles-space="site.inline"></div>
<script src="jttp://secupgrade.com/adnetworking/nickles.js"
        data-nickles-publisher="pub_…"
        data-nickles-site="your-name.custom"
        data-nickles-space="site.inline"
        data-nickles-token="…"></script>
```

- **The JS file is a manifest.** `nickles.js` is a frozen object containing the network, publisher,
  site, space, and token. It is never fetched or executed by a sealed page. The publisher recognises
  it while sealing and replaces it with a native ad block. No script engine, no remote creative, no
  third-party SDK.
- **The token is the marriage.** It is HMAC-bound to the publisher id, host, and space. Publishing on
  another host, editing the space, or copying a token from another profile fails closed.
- **Layout.** One 240 × 140 creative inside a 260 × 150 card, left-aligned in the page flow. The `AD`
  label is supplied by the client, not the creative. The card is keyboard-focusable; `Enter` opens
  the destination through the clearnet confirmation sheet.
- **Rotation.** The slice uses a signed local catalog and a deterministic 10-minute bucket. Production
  fetches a signed slot from `/v1/ads/slot/site.inline` and verifies the creative hash before display.
- **No injection.** If the page did not include a valid snippet, no ad appears. Publishers cannot
  inject into someone else's page, and the home slot never appears inside a sealed page.

## 3. Ranking: the donation list

Public, signed, and reproducible:

```
weight_i = donation_i^0.85 × decay(age_i) × quality_i
share_i  = weight_i / Σ weight
decay(age) = 0.5 ^ (age_days / 30)
quality    ∈ [0.5, 1.0]   // manual: policy compliance, creative legibility
```

- `donation_i` is the **prepaid slot-time** purchased, in USD-equivalent at the time of payment (or NKL
  testnet units, which are displayed separately and never mixed with real budgets).
- The ranking table is published at `/v1/ads/ranking` and rendered on `jttp://ads` (public). Users can
  see exactly who is buying attention and for how much.
- **No bid manipulation:** one campaign per advertiser per 24 h; self-dealing is allowed but pointless
  (it buys rotation, which is the product).
- Rotation within a share is deterministic per 10-minute bucket: `bucket = floor(unix/600)`, select by
  weighted rendezvous hash so all clients show the same ad in the same bucket (no per-user
  fingerprint, reproducible for audits).

## 4. Creative spec

| Rule | Detail |
|------|--------|
| Format | Native text/vector creative for the home footer; publisher-site images PNG/WebP, ≤ 200 KB, 240×140, sRGB |
| Text | ≤ 40 characters headline + ≤ 60 support; must render at 11 px |
| Motion | none inside the image; the container may fade |
| Links | one HTTPS destination, shown on hover; no redirector, no UTM with user ids |
| Script | forbidden |
| Publisher loader | `nickles.js` is a signed placement manifest; it never executes in a sealed page |
| Site slot | text creative from the signed local catalog in the slice; the image spec below applies to the owned home slot |
| Fonts | embedded as pixels; no remote fonts |
| Alt text | required, ≤ 120 chars |
| Landing page | must work without JS (we open it in the JS-off modal) |
| Disclosure | `AD` label supplied by the client, not the creative |

Rejected: malware, scams, "get rich", adult content, gambling (except where legally licensed and
geo-gated — not in 1.0), political/campaign ads, crypto investment promises, anything impersonating
Kickl or a browser warning, anything with fake download buttons or fake close controls.

## 5. Buying a placement

```
1. Advertiser opens jttp://ads (public, no account) → "Buy a slot"
2. Chooses duration (7/30/90 days) and a donation amount (minimum 25 USD-equivalent/week)
3. Uploads a creative → automated checks (size, format, text length, malware scan)
4. Pays: BTC invoice (unique address, 1 confirmation) or NKL (testnet, labelled)
5. Moderation (manual, ≤ 72 h) → approved or rejected with a reason; rejected → refund
6. Campaign becomes active for its purchased window; ranking recomputed hourly
7. Public entry appears on the ranking page: advertiser name, amount, window
```

- **No account.** The invoice id + a random claim token is the only credential; the advertiser gets a
  status page URL with the token in the fragment (never sent to the server in a query string).
- **No reports with user data.** The advertiser sees slot-time purchased and the rotation share, plus
  aggregate "served buckets" computed from the public ranking formula, not from client beacons.

## 6. Privacy accounting

| Data | Collected? | Where |
|------|-----------|-------|
| Impression | **no** | — |
| Click | **no** | the link goes straight to the advertiser; no redirector |
| Cookie / device id | **no** | — |
| Geo | **no** | — |
| Creative fetch | yes, as a content-addressed blob from the CDN; publisher-slot creatives are resolved locally in the slice | CDN sees an IP like any download |
| Publisher token | yes, bound to site + space; stored encrypted in the active profile's `ads/publishers.kpf` | local profile |
| Campaign payment | yes, on-chain (public) | blockchain |

The CDN inevitably sees an IP when the creative is fetched. Mitigation: creatives are cached locally
with the home bundle and refreshed at most once per 10-minute bucket; the client may fetch through the
same transport as registry snapshots. We state this in the privacy page.

## 7. Moderation

- Queue: automated scan → human review → approve/reject.
- SLA: 72 h best effort; rejection reasons are published in a code list (`POL-01 misleading claim`,
  `POL-02 malware`, …).
- Repeat offenders are banned by payout address and by creative hash.
- A public takedown log (advertiser name, reason, date) with an appeal window of 7 days.
- The operator (you) is the moderator in 1.0; the SLA and the log are the accountability.

## 8. Data model (server)

```sql
CREATE TABLE ad_campaigns (
  id            CHAR(26) PRIMARY KEY,             -- ULID
  advertiser    VARCHAR(80) NOT NULL,             -- public display name
  contact_hash  BINARY(32) NOT NULL,              -- hashed contact, not displayed
  placement     VARCHAR(48) NOT NULL DEFAULT 'home.footer',
  starts_at     BIGINT UNSIGNED NOT NULL,
  ends_at       BIGINT UNSIGNED NOT NULL,
  donation_usd  DECIMAL(12,2) NOT NULL,
  donation_nkl  BIGINT UNSIGNED NULL,
  quality       DECIMAL(3,2) NOT NULL DEFAULT 0.8,
  status        ENUM('pending_payment','review','active','rejected','expired') NOT NULL,
  claim_hash    BINARY(32) NOT NULL,              -- SHA-256(claim token)
  created_at    BIGINT UNSIGNED NOT NULL
);

CREATE TABLE ad_creatives (
  id          CHAR(26) PRIMARY KEY,
  campaign_id CHAR(26) NOT NULL,
  sha256      CHAR(64) NOT NULL,
  width       SMALLINT UNSIGNED NOT NULL,
  height      SMALLINT UNSIGNED NOT NULL,
  alt_text    VARCHAR(120) NOT NULL,
  dest_url    VARCHAR(512) NOT NULL,
  review_note VARCHAR(400) NULL,
  FOREIGN KEY (campaign_id) REFERENCES ad_campaigns(id)
);

CREATE TABLE ad_payments (
  id          CHAR(26) PRIMARY KEY,
  campaign_id CHAR(26) NOT NULL,
  asset       ENUM('btc','nkl') NOT NULL,
  address     VARCHAR(96) NOT NULL,
  amount      VARCHAR(32) NOT NULL,
  txid        VARCHAR(96) NULL,
  confirmations INT UNSIGNED NOT NULL DEFAULT 0,
  paid_at     BIGINT UNSIGNED NULL,
  FOREIGN KEY (campaign_id) REFERENCES ad_campaigns(id)
);

CREATE TABLE ad_takedowns (
  id          BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  campaign_id CHAR(26) NOT NULL,
  reason_code VARCHAR(8) NOT NULL,
  note        VARCHAR(400) NOT NULL,
  created_at  BIGINT UNSIGNED NOT NULL
);

CREATE TABLE ad_publishers (
  id          CHAR(26) PRIMARY KEY,             -- pub_…
  site        VARCHAR(80) NOT NULL,             -- jttp host the token is bound to
  space       VARCHAR(48) NOT NULL DEFAULT 'site.inline',
  token_hash  BINARY(32) NOT NULL,              -- hash of the signed placement token
  created_at  BIGINT UNSIGNED NOT NULL,
  status      ENUM('active','revoked') NOT NULL DEFAULT 'active',
  UNIQUE KEY uq_publisher_site_space (site, space)
);
```

## 9. API

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/v1/ads/ranking` | public ranking table (signed) |
| GET | `/v1/ads/slot/{placement}` | current creative for a 10-min bucket (signed, cacheable) |
| POST | `/v1/ads/publisher` | mint a site-bound publisher placement (public, PoW + rate limit) |
| POST | `/v1/ads/campaign` | create campaign (public, PoW + rate limit) |
| POST | `/v1/ads/campaign/{id}/creative` | upload creative (multipart, ≤ 200 KB) |
| POST | `/v1/ads/campaign/{id}/pay` | create invoice (BTC/NKL) |
| GET | `/v1/ads/campaign/{id}/status?token=` | status page (token in fragment, not query) |
| POST | `/v1/ads/takedown` | operator-only, signed |

Slot response:

```json
{
  "bucket": 2988001,
  "campaign": "01J…",
  "creative": { "url": "https://cdn…/ab12.png", "sha256": "…", "w": 240, "h": 140,
                "alt": "…", "dest": "https://…" },
  "label": "AD",
  "expires": 1792800600,
  "sig": "b64url:3373"
}
```

The client verifies the signature and the creative hash before displaying. A mismatch → show the empty
`AD` strip and log a security event.

## 10. Abuse and fraud

| Abuse | Control |
|-------|---------|
| Fake payment screenshot | on-chain verification only |
| Malware creative | scan + human review + hash blocklist |
| Impersonation | name/creative review; ban by payout address |
| Ranking wash-trading | it only buys rotation, which is the product; no refunds for self-dealing |
| Slot cloaking | the served creative is signed and hash-pinned; no per-user variation |
| Click fraud | not measurable, therefore not a product metric |
| Ad-blocking | ads are first-party and local; there is no ad-block surface, but the slot is dismissible |

## 11. Open questions

- Q-04 revenue split (ads are 100% platform revenue by default).
- Whether to accept fiat (Stripe) at all — requires KYC/chargeback handling; default **no**.
- Whether to allow alcohol/gambling with geo-gating — default **no** in 1.0.
- Minimum donation and refund policy for rejected campaigns.

## 12. Implementation checklist (2026-10-25)

- [x] Local publisher creation, profile-encrypted publisher records, and host/space-bound HMAC token.
- [x] Publish-time snippet validation and a native, labelled, deterministic local ad slot.
- [ ] Creative upload, malware/format scan, moderation queue, appeals, and takedown log.
- [ ] Server campaign/payment APIs and reproducible public donation ranking.
- [ ] Asymmetrically signed server slot response, key pinning, expiry checks, and creative hash verify.
- [ ] Public policy pages for ads, publishers, moderation, privacy, payments, and takedowns.
- [ ] Packet capture confirming no impression/click beacons or unintended network requests.

The current slot token and response authentication are local profile-key HMACs. They are not
server-issued asymmetric signatures and cannot substitute for the production signed-response flow.
