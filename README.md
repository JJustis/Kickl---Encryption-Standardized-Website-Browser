# KICKL BROWSER — Project "PINWHEEL"

A privacy-first browser for **Windows and Linux** that ships its own encrypted mail, encrypted live
chat, a public domain registry ("phonebook"), a steganographic page vault, an instant site-claim flow,
and an optional Nickles economy — in one C# application, with a slim chrome so the website gets almost
the whole window.
**Status:** Browser-first vertical slice — screen 1 (browser) and screen 2 (publications and saved items) run in
Avalonia; the handheld emulator is deferred. The KML surface, sealed blobs, local registry/PFS lookup,
unknown-name creation template, `.rss` story template, saved reading list, direct origin-device tunnel, and
sanitized inline HTML/SVG visuals are implemented.
**Working name:** Kickl Browser (trademark screen pending) · **Prepared:** 2026-10-24
**Canonical scheme:** `jttp://name.custom/`

## Install

Short version (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```bash
git clone https://github.com/JJustis/Kickl---Encryption-Standardized-Website-Browser.git
cd Kickl---Encryption-Standardized-Website-Browser
dotnet build Kickl.slnx -c Release
dotnet run --project src/Kickl.Browser -c Release -- --demo --data .kickl-demo    # sign in with 1234
```

Windows users can also run the prebuilt `KicklBrowser-windows-x64.exe` from the download page. The registry server,
the CA and claiming names are optional. **Full instructions, requirements and troubleshooting: [INSTALL.md](INSTALL.md).**

### Current implementation

- `Kickl.Browser` is the primary app: tabs, omnibox, history, sealed-page rendering, blob unlock,
  named local profiles with per-profile user keys, lock/unlock, publications feed, saved reading list,
  RSS sources, and a browser-space editor for unknown names and
  `.rss` stories.
- A runnable **Pinwheel registry server** lives in `server/` (PHP 8.2 + MySQL/MariaDB): signed
  `head`, `snapshot`, `delta`, and public-key endpoints plus operator-only registration. The hybrid
  Ed25519 + ML-DSA-65 key stays in `tools/Kickl.RegistrySigner`, which signs snapshots and deltas
  offline; the API stores and serves exactly those bytes.
- `kickl registry sync --url URL --key FILE` fetches and verifies the phonebook into
  `shared/registry.json`, using a full snapshot first and signed deltas afterwards. See
  [`server/README.md`](server/README.md) for XAMPP/MySQL setup and the current limits.
- The sign-in screen can create and switch between named local profiles. Each profile has its own
  Argon2id-wrapped master key and data directory; profile names remain visible in directory names.
- The right-side news stream reads `C:\xampp\htdocs\rssfeed.xml` at startup and every five minutes.
  Cards are sanitized, deduplicated by feed IDs, and remain inert until opened; the app reads the
  local publications file directly.
- The handheld emulator also keeps this publications XML on its always-visible top/front screen,
  refreshing every five minutes independently of the locked browser session.
- With no address looked up, the start page is a **split home**: the first half is the games array
  (native tic-tac-toe and memory-match games are playable; sandboxed wasm-1 games are queued), and the second half
  is the **LegendDX PFS tag search**. The query and results travel inside one AES-256-GCM session;
  hits stay inert until confirmed.
- Webmasters can opt a sealed site into **NCKL ads** at
  `jttp://secupgrade.com/adnetworking/publisher`. Minting returns a site-bound loader, `nickles.js`,
  and an ad-space snippet. The loader is a signed manifest, never executed; publishing verifies it
  and stores one native `<ad>` block. The `AD` label is always visible, rotation is local, and a
  snippet copied to another host fails closed.
- The owned home surface also has the `home.footer` slot: the local catalog selects the same
  creative for each ten-minute bucket, the bar spans the full browser-page footer above the status
  bar (never overlapping content), and its dismiss-to-label preference stays in the local profile.
- `.com` and custom pages may be authored as **inline SVG inside an HTML `<div>`** instead of a CSS
  dialect. The sanitizer removes scripts, forms, event handlers, external requests, and HTML style
  attributes; the renderer maps a small static HTML subset and inline SVG to Avalonia. `.rss` keeps
  its fixed story template and does not accept inline HTML/SVG. See `docs/07` §2.
- A **new custom name starts as a complete editable example page**, with a live preview in the
  editor. The author learns the format by changing a working page rather than starting from blank
  markup.
- An **RSS story is authored as XML**: the editor shows an `<rss><channel><item>` template; the
  `<title>`, `<description>`, `<category>`, `<pubDate>`, `<location>`, and optional `<enclosure>`
  are parsed into the fixed story fields at publish time.
- Images embedded in inline SVG are **sealed automatically**: data-URL raster bytes become
  AES-256-GCM document blobs, the markup keeps only `kickl-blob:<id>`, and the reader clicks an
  unlock control before the image renders.
- Direct article fetch uses the registry-published origin RSA key: RSA-OAEP wraps a fresh AES-256
  key, HKDF splits request/response keys, AES-256-GCM authenticates both frames, and RSA-PSS signs
  the response. The registry SHA-256 is still checked first; mismatches fail closed.

- **Claiming a name** costs Bitcoin at the BTC shop, which hands the buyer an **ownership certificate**
  (`.kcert`). Until then every name is free for anyone to create and edit; afterwards `jttp://name/edit` asks for
  the certificate. See `docs/17` and `docs/23`.
- **The Kickl CA** (`tools/Kickl.Ca`, `server/secca`) countersigns receipts automatically, writes every seal to an
  append-only transparency log, and a sealed site shows **VERIFIED OWNER** to every visitor. Every CA signature is
  hybrid Ed25519 + ML-DSA-65. `test-claims.bat` runs the whole flow against a throwaway test shop and test CA with no
  Bitcoin. See `docs/23-certificate-authority.md`.
- **Owner binding.** When you seal a claim you can choose an **owner passphrase**. The browser sends the CA only an HMAC
  of it (Argon2id key, bound to the name and claim); the CA signs and logs that inside the seal. From then on holding the
  `.kcert` file is not enough to edit the name: the editor must also give the passphrase. Copies the browser saves are
  themselves passphrase-sealed. Best of all, type the passphrase on the BTC shop page when you order: the shop then signs the binding into the
  certificate itself, so it cannot be stripped, swapped, or beaten by someone sealing first.
- **Direct mail is TLS 1.3 with a pinned key.** Each device's mail endpoint is `https://host:port#pin=...`, where the pin
  is a hash of that device's own TLS key and is covered by the identity's post-quantum signature, so a sender accepts only
  that key and refuses plain-http or unpinned endpoints. The listener caps connections and times out slow peers.
- **FreeChat remembers member keys and signs messages.** The first ML-KEM key seen for a username is pinned in your
  encrypted profile; a different key later is shown as KEY CHANGED and gets no room secret until you compare fingerprints
  and accept it. Every message you send carries an ML-DSA-65 signature (in a trailer old clients ignore), and each message
  you receive is labelled signed, new key, key changed, forged, or unsigned.
- **Owner binding.** When you seal a claim you can choose an **owner passphrase**. The browser sends the CA only an HMAC
  of it (Argon2id key, bound to the name and claim); the CA signs and logs that inside the seal. From then on holding the
  `.kcert` file is not enough to edit the name: the editor must also give the passphrase. Copies the browser saves are
  themselves passphrase-sealed. Seal with the passphrase the first time: a claim sealed without one stays unbound.
- **Direct mail is TLS 1.3 with a pinned key.** Each device's mail endpoint is `https://host:port#pin=…`, where the pin
  is a hash of that device's own TLS key and is covered by the identity's post-quantum signature, so a sender accepts only
  that key and refuses plain-http or unpinned endpoints. The listener caps connections and times out slow peers.
- **FreeChat remembers member keys.** The first ML-KEM key seen for a username is pinned in your encrypted profile; a
  different key later is shown as KEY CHANGED and gets no room secret until you compare fingerprints and accept it.
- **Games** are sealed WebAssembly modules run in a capability-free sandbox (no imports, fuel-metered, memory-capped);
  try `jttp://game-lab.custom`. See `docs/22-wasm-games.md`.
- **Public and protected content.** Page text and public blobs are encrypted at rest but open to every reader;
  `access="protected"` blobs need the site's access key. See `docs/20`.

### Try the playable demo

From the repository root, run `dotnet run --project src/Kickl.Browser -- --demo --data .kickl-demo`.
The demo profile starts at the sign-in gate; enter the fixed sample key `1234` to unlock it. Use the
demo only for experimentation, never for personal data. The shell scales down to the available
screen (including a TV), so the whole window stays visible. Play Tic-Tac-Toe or Memory Match from the ARCADE pane, inspect the inline SVG demo at
`jttp://svg-lab.custom`, try the sealed-page unlock at `jttp://sealed-lab.custom`, or view the local
publisher slot at `jttp://ad-lab.custom`. Snake and 2048 are visible placeholders, not playable games.
See [`docs/13-home-legenddx-arcade.md`](docs/13-home-legenddx-arcade.md) for the current activity list.

Screenshots: `artifacts/home.png` (games array + PFS search home),
`artifacts/home-search.png` (a PFS tag lookup with inert results),
`artifacts/nckl-publisher.png` (the webmaster publisher console),
`artifacts/nckl-ad-site.png` (an opt-in native ad slot in a sealed page),
`artifacts/browser-screen.png` (browser + news rail), `artifacts/svg-inline.png` (sealed inline SVG
page), `artifacts/sealed-inline.png` (inline SVG with a click-to-unlock sealed image),
`artifacts/example-site.png` (the generated example page in the editor), and
`artifacts/rss-xml-editor.png` (the RSS XML template).

---

## Read in this order

| # | Document | What it is | Read if you are… |
|---|----------|-----------|------------------|
| 0 | [`README.md`](README.md) | This map + decisions + status | everyone |
| 0.1 | [`docs/00-decisions.md`](docs/00-decisions.md) | Decision log (ADR-style), including the review answers | everyone |
| 1 | [`REPORT.md`](REPORT.md) | **Master report.** Vision, requirements, reality-check, architecture, phase plan, gates | everyone |
| 2 | [`docs/01-product-scope.md`](docs/01-product-scope.md) | Personas, journeys, scope, non-goals, metrics | product |
| 3 | [`docs/02-architecture.md`](docs/02-architecture.md) | Cross-platform process model, solution layout, data flow, threading, storage | engineering |
| 4 | [`docs/03-cryptography.md`](docs/03-cryptography.md) | One AEAD (AES-256-GCM), hybrid PQ KEM, ratchets, PIN/keystore, test vectors | security |
| 5 | [`docs/04-pinwheel-network.md`](docs/04-pinwheel-network.md) | Polling, snapshots, peers, relays, mail transport, PHP/MySQL API | backend |
| 6 | [`docs/05-registry-identity.md`](docs/05-registry-identity.md) | `jttp://`, `.custom` names, `kb1…` addresses, certs, cookies, instant claims | security |
| 7 | [`docs/06-shell-ux.md`](docs/06-shell-ux.md) | Slim chrome, tabs-in-omnibox, animations, home split, dock, panic, ad slot | design / front-end |
| 8 | [`docs/07-blob-markup-cache.md`](docs/07-blob-markup-cache.md) | KML markup, KSS styles, blob unlocking, PNG vault, ratcheting cache | engineering |
| 9 | [`docs/08-mail-freechat.md`](docs/08-mail-freechat.md) | Mailbox model, contact bundles, MLS group chat, presence | engineering |
| 10 | [`docs/09-nickles.md`](docs/09-nickles.md) | Testnet ledger, plugin packaging, payments, legal gate | product / legal |
| 11 | [`docs/10-security-program.md`](docs/10-security-program.md) | Threat model, CVE intake, patch SLAs, hardening, supply chain | security |
| 12 | [`docs/11-roadmap-gates.md`](docs/11-roadmap-gates.md) | 18 phases, exit criteria, effort, dependencies | everyone |
| 13 | [`docs/12-sentinel-cve-watcher.md`](docs/12-sentinel-cve-watcher.md) | The AI-assisted CVE watcher that alerts the Pinwheel server | backend / security |
| 14 | [`docs/13-home-legenddx-arcade.md`](docs/13-home-legenddx-arcade.md) | Home = first half games array, second half LegendDX PFS search; fame mechanics | product / front-end |
| 15 | [`docs/14-plugins-market.md`](docs/14-plugins-market.md) | Plugin sandbox, capabilities, signing, marketplace, BTC/NKL sales | engineering / security |
| 16 | [`docs/15-ad-network.md`](docs/15-ad-network.md) | NCKL-only ads, home slot, opt-in site-bound publisher slots, donation-ranked placements | product / backend |
| 17 | [`docs/16-input-privacy-and-sessions.md`](docs/16-input-privacy-and-sessions.md) | Per-keystroke AES, mouse-track ban, device-query ban, per-identity data, entry gate | security / engineering |
| 18 | [`docs/17-ownership-titles.md`](docs/17-ownership-titles.md) | Paid titles, paired credential dongles, AES ownership token, badge, transfer | security / product |
| 19 | [`docs/18-sealed-pages-and-stego-cache.md`](docs/18-sealed-pages-and-stego-cache.md) | Per-region sealed pages, PNG LSB carrier, polymorphic re-seal | engineering / security |
| 20 | [`docs/19-hardware-device.md`](docs/19-hardware-device.md) | Two-screen handheld: front RSS, four USB ports, two mini-SD holders, paired dongles, MicroCore | hardware / product |
| 21 | [`docs/20-encryption-map.md`](docs/20-encryption-map.md) | Visual map of the encryption flow and the complete plaintext inventory | everyone |
| 22 | [`docs/21-live-index-and-keys.md`](docs/21-live-index-and-keys.md) | PFS on every Kickl link, temporal keys, tagged live index, click prompt | security / backend |
| 23 | [`docs/22-wasm-games.md`](docs/22-wasm-games.md) | Sealed WebAssembly games in a capability-free sandbox | engineering / security |
| 24 | [`docs/23-certificate-authority.md`](docs/23-certificate-authority.md) | The Kickl CA, seals, transparency log, owner binding | security / backend |
| 25 | [`docs/24-security-audit.md`](docs/24-security-audit.md) | In-house security audit: findings, fixes, and what is still open | security |
| 26 | [`docs/25-doc-review.md`](docs/25-doc-review.md) | Which documents were re-read against the code, and their status | everyone |
| — | [`CHECKLIST.md`](CHECKLIST.md) | **Tickable master checklist** — one line per deliverable | everyone |
| — | [`reference/api.md`](reference/api.md) | Kickl HTTP/PFS API v1 | backend |
| — | [`server/README.md`](server/README.md) | Registry server setup, endpoints, and current limits | backend |
| — | [`reference/schema.sql`](reference/schema.sql) | MySQL 8 schema | backend |

---

## Decisions locked in this round

| ID | Decision |
|----|----------|
| D1 | **Windows + Linux** are both 1.0 targets. |
| D2 | **`jttp://`** is the canonical scheme (typing `http://x.custom` rewrites to it). |
| D3 | **Own registry** hosted at secupgrade.com; you operate PHP/MySQL and patch them. |
| D4 | Product name for now: **Kickl Browser**. |
| D5 | Nickles ships as an **optional downloadable plugin**, opened through its quote panel. |
| D6 | Site registration is **instant and in-flow**: an unknown URL prompts "claim and build it now". |
| D7 | Home page is **LegendDX search (half) + user-made arcade (half)**. |
| D8 | Ads are **only** allowed through the NCKL ad network; the home slot is a full-width footer bar above the status bar, ranked by donation, and webmasters may opt in to a site-bound `site.inline` slot with a signed loader manifest. |
| D9 | Plugins may be sold for **BTC, NKL, or free**. |
| D10 | An **AI CVE watcher** runs on a timer, reads advisories, and alerts the Pinwheel server with a plain-language protection summary. |
| D11 | **No mouse tracking, no plaintext keystrokes at rest**: per-keystroke AES-256-GCM cells bound to the session secret. |
| D12 | **Devices are not queryable**; each person gets a fully separate encrypted browser profile; first use requires a user key ("sign in" or "create identity"). |

Still open: audit budget, exact Linux packaging targets, whether LegendDX queries may leave the device,
plugin revenue split, and the legal posture for NKL (see `docs/00-decisions.md` §3).

---

## The one-paragraph version

Kickl is a C# (.NET 10 / Avalonia) shell with two rendering surfaces: a **sealed surface** for
`jttp://name.tld` documents built from KML markup, where every block of content arrives as an
individually encrypted blob that only unlocks with the local user's PIN; and a **clearnet lane** for
`http(s)://` sites that opens the system browser by default, with an optional contained mini-browser
(Chromium on Windows, WebKitGTK on Linux, JavaScript off, disposable profile). `jttp://` is our own
DNS namespace — never a replacement for HTTP. Names resolve from a **public, signed, Merkle-rooted
phonebook** that the client mirrors in full and resolves locally, so browsing never leaks per-domain
queries to the registry. Mail and chat ride the same **Pinwheel** transport: store-and-forward ciphertext envelopes,
hybrid post-quantum handshakes, and short-lived group epochs. A bottom-right dock holds both. A
hold-to-arm **panic button** crypto-erases every store. The home page is first half games array,
second half LegendDX PFS tag search. Nickles is an optional plugin, and the only advertising system
is the Nickles network.

---

## Non-negotiable engineering rules

1. **No invented cryptography.** Only FIPS 203/204/205, AES-256-GCM, HKDF-SHA-512, Argon2id,
   RSA-OAEP/PSS (ownership dongles), AES-256-GCM, HPKE (RFC 9180), MLS (RFC 9420), Noise.
2. **The PIN never leaves the device.** Device identity is proven by signing a challenge.
3. **Plaintext is never persisted.** Only ciphertext touches disk. Clearnet cache is disposable.
4. **Privacy claims are measured, not asserted.** Every "no one can see" claim has a threat-model row,
   a test, and a stated limit.
5. **Plugins and ads never get ambient authority.** No plugin can read keys, mail, or the vault; no ad
   can run script, track, or appear over third-party page content.
6. **Every phase has an exit gate.** No phase starts until the previous gate is signed off.
