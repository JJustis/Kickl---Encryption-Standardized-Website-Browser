# KICKL — Master Implementation Report

**Codename:** PINWHEEL · **Build target:** Windows 10 22H2 / 11 and Linux (x64; arm64 best-effort)
**Stack:** C# 14 · .NET 10 LTS · Avalonia 11 shell · WebView2 (Windows) / WebKitGTK 6 (Linux) ·
SQLite/SQLCipher · PHP 8.4 + MySQL 8
**Version of this document:** 1.1 · 2026-10-24 (review round 1 folded in)
**How to use it:** read §1–§6 for orientation, then work `CHECKLIST.md` phase by phase. Every
checkbox in `CHECKLIST.md` traces to a numbered requirement (`R-xx`) or gate (`G-x`) here.

---

## 1. Vision

Build a browser where *your data is yours at rest and in transit*: a sealed web of
`name.custom` documents whose every block is ciphertext until **you** unlock it; mail and chat
inside the same window; a phonebook of domains that anyone can read but nobody can silently edit;
and a panic button that destroys the keys instead of pretending to scrub an SSD.

The visual identity is "sleek cyberdeck": near-black surfaces, phosphor and cyan accents, hairline
rules, monospace metadata, restrained glitch transitions — but accessible and quiet by default.
The website viewport is the product: a 36 px chrome rail holds tabs *and* the omnibox.

**What success looks like at 1.0**

- Cold start < 1.5 s; tab switch < 120 ms; sealed page first paint < 400 ms on a 4-core laptop.
- 100% of sealed content persisted only as ciphertext; verified by a disk-forensics test.
- Zero telemetry; the app makes no network call the user did not initiate.
- Independent crypto review with no Critical/High findings open.
- A user can go from install → identity → add a friend → send mail → chat in under 5 minutes.

---

## 2. Normalized requirements

Your brief, restated as testable requirements. "Sealed" = the Kickl surface (`jttp://`). "Clearnet" =
ordinary web in the separate lane (system browser, or the contained mini-browser).

| ID | Requirement | Notes / where specified |
|----|-------------|-------------------------|
| R-01 | C# browser that works out of the box on **Windows and Linux** | §4, `docs/02`, ADR-001 |
| R-02 | Built-in encrypted email | `docs/08` |
| R-03 | Built-in Skype-like live chat, opened from bottom-right dock | `docs/06`, `docs/08` |
| R-04 | Email encrypted behind a custom handshake + AES-256 | Replaced with hybrid PQ handshake + AEAD suite, `docs/03` |
| R-05 | "Trio of ciphers" selected for quantum resistance | Reframed as one AES-256-GCM AEAD plus hybrid X25519/ML-KEM-768 and Ed25519/ML-DSA-65; §5.2, C1 |
| R-06 | Safety work during construction: CVE hunt, patches where possible | `docs/10`, phase gates |
| R-07 | Friend linking: friends of friends, export/import contact bundles | `docs/08` §5 |
| R-08 | Public domain registry inside the browser, works like a phonebook | `docs/05` |
| R-09 | Live updates for registered sites | signed snapshots + deltas, `docs/04` §4 |
| R-10 | Modern, sleek UI with a "hacker world" look | `docs/06` |
| R-11 | Automatic **or** manual delete-all, panic button, per-app scopes | `docs/06` §8, crypto-erase `docs/03` §9 |
| R-12 | Maximum viewport; slim search bar; tabs inside it | 36–40 px rail, `docs/06` §3 |
| R-13 | Fancy animated buttons with light changes | design tokens + motion spec, `docs/06` §5 |
| R-14 | Custom cookie + certificate checking for `.custom` domains | `docs/05` §6–7 |
| R-15 | Pinwheel polling peer routing for chat + mail | `docs/04` |
| R-16 | Handshake then AES-256 (×3 ciphers) on every message | One AES-256-GCM AEAD with hybrid PFS handshake and per-message ratchets; `docs/03` §4–6 |
| R-17 | Loaded page data encrypted with user key; re-open cached copy without re-querying | Blob vault, `docs/07` |
| R-18 | Each site becomes a local "DDNS" record of where to go | local registry mirror, `docs/04` §3 |
| R-19 | PHP API over MySQL at secupgrade.com, polled live | `reference/api.md`, `reference/schema.sql` |
| R-20 | URLs encrypted RSA-style via handshake with domain owner; PFS standard | Replaced by HPKE + per-fetch ephemeral keys, §5.5 |
| R-21 | Clearnet opens in a separate browser: system default by default, or a JS-locked mini browser in a modal, expandable | `docs/06` §7 |
| R-22 | Cryptocurrency integration; a currency called Nickles | Testnet only, `docs/09` |
| R-23 | Custom markup for CSS rules | KML markup; inline SVG/HTML visual block instead of a CSS requirement for `.com`/custom pages, `docs/07` §2 |
| R-24 | Blob-based content; click to decrypt with PIN; per-part visibility | `docs/07` §3 |
| R-25 | Polymorphic ratchet encryption on the cached copy; opens change ciphertext, not data | HKDF ratchet per blob, `docs/03` §7 |
| R-26 | Cached page is a PNG of the whole site, hidden by steganography, master password + PIN per site | Container format + honest capacity model, `docs/07` §6 |
| R-27 | Only opened blobs are visible; unopened stay sealed in the saved copy | `docs/07` §5 |
| R-28 | "Reload the real site from the owner's computer" button in the URL bar | signed fresh fetch, `docs/07` §7 |
| R-29 | Fancy but non-intrusive mail; collapsed dock with two lobes (chat / mail) | `docs/06` §6 |
| R-30 | IDs look like phone numbers / bitcoin addresses, derived from a signature of the person's data | `kb1…` Bech32m addresses, `docs/05` §3 |
| R-31 | Optional per-message key pair for extra privacy | per-thread key + ephemeral prekeys, `docs/08` §6 |
| R-32 | Everything follows standards | standards register, `docs/02` §8 |
| R-33 | `jttp://` is the canonical scheme | ADR-002, `docs/05` §1 |
| R-34 | Unknown URL offers "claim and build it now" | ADR-006, `docs/05` §8 |
| R-35 | Home = first half games array, second half LegendDX PFS tag search | ADR-007, `docs/13` |
| R-36 | Ads only via Nickles network; first slot lower-left, ~1 inch above bottom | ADR-008, `docs/15` |
| R-37 | Plugins can be free / BTC / NKL; Nickles is a plugin with a quote panel | ADR-005/009, `docs/14` |
| R-38 | AI CVE watcher alerts the Pinwheel server with protection steps | ADR-010, `docs/12` |
| R-39 | Own registry, operated at secupgrade.com | ADR-003, `docs/04` |
| R-40 | Product name Kickl Browser; Linux parity is a release gate | ADR-001/004, `docs/11` |
| R-41 | No mouse tracking, no click maps, no pointer storage | ADR-011, `docs/16` §5 |
| R-42 | Per-keystroke AES-256-GCM cells; no plaintext keystrokes at rest | ADR-011, `docs/16` §4 |
| R-43 | Devices cannot be queried/fingerprinted by Kickl or sealed sites | ADR-011, `docs/16` §6 |
| R-44 | Every person gets their own encrypted browser data; user key required at entry | ADR-011, `docs/16` §2, §7 |
| R-45 | Every Kickl-to-Kickl link uses fresh ECDH + AES-256-GCM; RSA never transports keys | ADR-014, `docs/21` §1 |
| R-46 | Keys are temporal: per opening and per connection; the ownership key stays stable | ADR-014, `docs/21` §2 |
| R-47 | Live index at `secupgrade.com/legenddxI2`: title, headline, timestamp, domain, required tags | ADR-014, `docs/21` §3 |
| R-48 | Result activation prompts the device; top screen is RSS + stylus mini search | ADR-014, `docs/19` §2, `docs/21` §4–5 |
| R-49 | Registry carries SHA-256 of the site package; receivers verify before render and count mismatches | ADR-014, `docs/21` §3.1 |
| R-50 | Page visuals use inline HTML/SVG in a div, not a CSS requirement; scripts/forms/external requests are stripped | ADR-015, `docs/07` §2 |
| R-51 | Images inside inline SVG are sealed with Pinwheel/document-key encryption and unlock only when clicked; `.rss` keeps its fixed story template and has no inline SVG | `docs/07` §2, ADR-015 |
| R-52 | Every new custom name starts from a complete editable example page; RSS stories are authored as an XML `<rss><channel><item>` template | ADR-015, `docs/07` §2 |
| R-53 | Webmasters opt into NCKL at `secupgrade.com/adnetworking/publisher`, mint a site-bound loader, and paste the snippet into their ad space; `nickles.js` is a manifest and never executes | ADR-016, `docs/15` §2a |
| R-54 | Publishing verifies the NCKL placement signature and stores one native `<ad>` block; a token copied to another site fails closed | ADR-016, `docs/15` §2a, `docs/07` §2 |

---

## 3. Reality check — where the brief must change to be buildable

This table is the most important page in the document set. Each row keeps your *intent* and replaces
a mechanism that is either impossible, unsafe, or legally hazardous.

| # | As briefed | Problem | What we build instead |
|---|-----------|---------|----------------------|
| C1 | "Pick a trio of ciphers for quantum protection" | Symmetric ciphers are not what quantum computers break; Grover only halves the margin. Cipher choice alone gives no PQ property. | **One AEAD** (AES-256-GCM) for every symmetric job, plus a real PQ layer: ML-KEM-768 (FIPS 203) hybridised with X25519, and ML-DSA-65 (FIPS 204) hybridised with Ed25519 for signatures. AES-256 keeps ~128-bit security under Grover. |
| C2 | "No one will see plaintext in this browser" | Plaintext exists in RAM, in the compositor, and on screen. No browser can prevent that. | Precise claims: no plaintext **at rest**, no plaintext **to the server**, no plaintext **on the wire**. RAM hygiene: no crash dumps, no swap for secrets (`VirtualLock`), zeroisation, process isolation. Limits documented in `docs/10` §2. |
| C3 | "URLs encrypted with RSA via a handshake with the domain owner's computer" | RSA is not PQ-safe; and the *name* still has to be resolvable, so end-to-end URL secrecy requires private information retrieval, not encryption. | Names resolve over a **PFS lookup channel** (ephemeral P-256 ECDH → HKDF → AES-256-GCM), and fetches are sealed to the owner's key. RSA is only device/title authentication, never content encryption. |
| C4 | "No one sees what you searched for unless they load first" | Search suggestions and clearnet queries inherently leave the device. | Local-only omnibox suggestions over the registry mirror; no keystroke telemetry; clearnet search is opt-in and routed through the mini-browser with a visible warning. |
| C5 | "Live polling so the registry never leaks" | Polling reveals IP + timing to the registry operator. | Full signed snapshot fetch (or shard) + local resolution, jittered 60–300 s polls, `If-None-Match`, optional Tor/OHTTP. Metadata limits stated. |
| C6 | "Currency leveraged into existence" | Creating purchasing power from software is not a technical act; it is a monetary/legal one and usually a securities or money-transmission problem. | Nickles = **signed testnet ledger** with an explicit "no value" banner, faucet, and no exchange integration until legal review (`docs/09`). |
| C7 | "Cache is a PNG of the whole site run through steganography" | A full-page screenshot cannot hide inside a *smaller* cover image; LSB capacity is ~1 bit/pixel (~1 MB for a 4K frame) and survives no re-encode. Claims of invisibility are false if the cover is the payload. | The page **is** the image: lossless full-page PNG → encrypted container (`KML-PNG`, AES-256-GCM) with an unencrypted thumbnail. LSB stego carries only the encrypted envelope (or the small manifest), not the page. Capacity math in `docs/07` §6 and `docs/18`. JPEG is rejected for LSB. |
| C8 | "Only blobs opened are visible; ciphertext updates on every open" | A key ratchet changes ciphertext, not the plaintext you already saw; and a local attacker with the PIN sees everything you opened. Also, re-encrypting on every open burns SSD writes. | Per-blob ratchet on a schedule (open-count or 24 h), atomic re-wrap, and an honest statement: ratcheting gives **key hygiene and forward secrecy for future blobs**, not retroactive hiding. Unopened blobs stay sealed — that part works and is specified. |
| C9 | "`.custom` is a domain you load over `http://`" | `.custom` is not a delegated TLD. Intercepting `http://`/`https://` for a fake TLD breaks TLS, HSTS, CSP, service workers, and trains users to accept MITM. `jttp` is a **customized DNS**, not an HTTP replacement. | Canonical scheme **`jttp://name.tld/`**, resolved only by our registry, registered as a *standard, secure* custom scheme. `http(s)://` always goes to the **separate clearnet lane** (system browser by default; contained mini-browser optional). No rewriting across lanes, no interception of real `https://`. |
| C10 | "PIN sent RSA-encrypted to the server to verify authenticity" (current secupgrade Pinwheel) | Server-side PIN verification = server can offline-grind the PIN; leaks a credential; defeats the local-only promise. | PIN unlocks a local keystore (Argon2id → KEK → master key, TPM/DPAPI-sealed). Device identity is proven by **signing a challenge**, never by revealing the PIN. |
| C11 | "PFS standard for every website query" | Store-and-forward mail cannot have true PFS for messages already delivered; and PFS does not hide metadata. | Ephemeral hybrid KEM per session + Double Ratchet/MLS for messaging; per-message keys deleted after use. Metadata limits documented. |
| C12 | "Friends of friends in a large contact list" | Automatic social-graph expansion is a privacy hazard (contact enumeration, graph disclosure). | Explicit, consent-based **vouches**: a friend may introduce a third party; you must accept; the introducer never sees your messages. Contact bundles are signed and revocable. |
| C13 | "Updates on its own" | Silent auto-update is the single largest supply-chain risk in a privacy product. | Updates are **signed, staged, and announced**; critical security updates may auto-apply only after signature + rollback-protection checks, with a visible log and a kill switch. |
| C14 | "Every connection is PFS, and a site just sends whatever it likes into search" | PFS protects keys, not metadata; search needs a bounded, validated record or it becomes an injection surface. | Per-link ephemeral ECDH; RSA signs only. Search records are publish-validated (`title`, `headline`, 1–8 tags), server-timestamped, tag-ranked, and capped at 50. Results are inert until confirmed. |
| C15 | "A SHA-256 in the registry proves the site is genuine" | A hash proves the bytes arrived unchanged. A malicious registry can serve different bytes with a matching hash. | The hash is a **transport-integrity commitment**, checked before parsing; a mismatch fails closed and increments the attack counter. Authorship stays with the ownership signature and the transparency log. |

---

## 4. System at a glance

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ KICKL.EXE  (Avalonia 11, .NET 10 — Windows + Linux)                          │
│                                                                              │
│  ┌──────────────── Chrome rail (36 px) ───────────────────────────────────┐  │
│  │ [tab][tab][+]  │  omnibox / jttp://…  │ ⟳ │ 🛡 │ ⧉ │ ⊗ panic │         │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│  ┌─────────────────────────── Viewport ──────────────────────────────────┐  │
│  │  Sealed surface: WebView2 #A (Win) / WebKitGTK #A (Linux)             │  │
│  │                  jttp:// · CSP-locked · JS off · network-off           │  │
│  │  — OR —                                                               │  │
│  │  Clearnet modal: WebView2 #B / WebKitGTK #B                           │  │
│  │                  https only · JS off by default · disposable profile   │  │
│  │  — OR —                                                               │  │
│  │  Home: LegendDX pane  +  Arcade pane  +  lower-left AD slot           │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│  ┌ bottom-right dock ────────────────────────────────────────────────────┐  │
│  │  ◉ FreeChat (MLS)                                   ✉ KicklMail        │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└───────────────┬──────────────────────────────────────────────────────────────┘
                │ named pipes + capability tokens (no shared memory of secrets)
┌───────────────▼──────────────────────────────────────────────────────────────┐
│ KICKL.CORE.EXE  (no UI, no listening socket)                                 │
│  Crypto · Keystore · Vault · Registry mirror · Mail · Chat · Sentinel client  │
│  Pinwheel transport · Sanitizer · KML parser · Plugin host · Audit log        │
└───────────────┬──────────────────────────────────────────────────────────────┘
                │ outbound only, TLS 1.3 + app-layer Noise/HPKE
┌───────────────▼──────────────────────────────────────────────────────────────┐
│ PINWHEEL SERVICE (secupgrade.com, PHP 8.2+ / 8.4 target + MySQL/MariaDB)     │
│  registry · rendezvous · mail relay · blobs · updates · transparency log      │
│  advisories (Sentinel) · arcade feed · plugin catalog · ad ranking · Nickles   │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Why two processes:** the web engines are the most exposed surface. The core holds keys and does all
decryption; the UI process never receives long-term key material. A compromised renderer gets a
short-lived capability token and already-decrypted fragments — not the vault, the identity key, or the
mail store.

**Why Avalonia:** WPF cannot run on Linux, and the shell must look and behave the same on both. Avalonia
gives us full control of the non-client area (needed for tabs-in-titlebar and a 36 px rail), custom
drawing and animation, per-monitor DPI, and a single XAML-ish codebase. WinUI 3 remains an option on
Windows only, which defeats the cross-platform requirement.

**Why two web engines:** writing a layout engine is a multi-year project and a security liability.
WebView2 (Chromium) on Windows and WebKitGTK on Linux give us a real sandbox, TLS, and rendering. An
`IWebSurface` abstraction hides the difference; every security property (custom scheme treated as
secure, JS disabled, request filtering, snapshot capture) is a contract test that both implementations
must pass. CEF is the fallback if WebKitGTK parity falls below 90% (Q-10).

---

## 5. Security architecture (summary — full detail in `docs/03`)

### 5.1 Identity and keys

```
Recovery phrase (24 words, BIP-39) ──► root seed
        │
        ├─ identity signing key   Ed25519 + ML-DSA-65  (long-lived, certifies)
        ├─ identity KEM key       X25519  + ML-KEM-768 (receives mail/blobs)
        ├─ device keys            per-device, revocable, certified by identity
        └─ vault key              random 256-bit, wrapped by PIN-derived KEK
PIN ──Argon2id(64 MiB, t=3, p=4, 16-byte salt)──► KEK ──AES-256-GCM──► vault key
vault key sealed additionally with TPM (DPAPI-NG / Windows Hello) where available
```

The **address** (`kb1…`, `docs/05` §3) is a checksummed encoding of a truncated hash of the identity
bundle — stable, verifiable, and impossible to impersonate without the private keys. It looks like a
Bitcoin address; a separate 8-digit **short code** gives the "phone number" feel and is only a
directory alias, never an authenticator.

### 5.2 One AEAD, and the PQ layer

| Layer | Primitive | Job | Why |
|-------|-----------|-----|-----|
| AEAD | **AES-256-GCM** | every symmetric job: records, blobs, cache, keystrokes, transport, ownership token | one primitive, one nonce rule, AES hardware everywhere |
| KEM | **X25519 + ML-KEM-768** → HKDF-SHA-512 | session and envelope key agreement | hybrid: classical + PQ; break requires both |
| Sig | **Ed25519 + ML-DSA-65**; RSA-3072-PSS on the ownership USB | identity, registry records, updates, title statements | hybrid signatures; downgrade-resistant |
| Wrap | **RSA-3072-OAEP-SHA256** | wraps the AES token on the owner USB | the USB key is the physical second factor |
| Hash | SHA-256 / SHA-512, BLAKE3 (content IDs) | integrity, addressing | standard |
| KDF | HKDF-SHA-512, Argon2id | key separation, PIN hardening | standard |
| Envelope | **HPKE RFC 9180** (base mode, hybrid KEM, AES-256-GCM) | sealed blobs and mail | standard, audited constructions |
| Ratchet | X3DH + Double Ratchet (1:1), **MLS RFC 9420** (groups) | messaging FS/PCS | standard, reviewed |
| Transport | **Noise_XX** with hybrid suite, inside TLS 1.3 | client↔service app-layer auth | defence in depth |

Nonce discipline is a hard rule: AES-256-GCM uses a per-key counter with a hard cap and key rotation
at 2³¹ messages; a random 96-bit nonce is used only for one-shot objects. There is no ChaCha/XChaCha
path and no cipher negotiation. Multi-recipient blobs use a per-recipient AES-GCM key plus a
key-commitment tag to avoid "invisible salamander" collisions.

### 5.3 Handshake (every session, every peer)

```
initiator                                                        responder
  │  eph X25519 + eph ML-KEM-768 ────────────────────────────────►│
  │◄──────────── eph X25519 + eph ML-KEM-768, cert, sig ──────────│
  │  combine → HKDF-SHA-512 → c2s/s2c keys (AES-256-GCM)          │
  │  transcript hash bound into every AEAD's AAD                  │
  │  ratchet step per message; old keys zeroised                  │
```

Every message: fresh AEAD nonce, sequence number, transcript-bound AAD, replay window. Rekey at
2²⁰ messages or 24 h, whichever first. Failure to verify a signature or a transcript hash is fatal
and surfaced in the UI — never silently downgraded.

### 5.4 What the server can and cannot see

| Item | Server sees | Server cannot see |
|------|-------------|-------------------|
| Registry | domain names, owner public keys, versions | page content, who visited |
| Mail relay | recipient mailbox hash, size class, timing | sender (sealed sender), subject, body |
| Chat rendezvous | peer IPs, presence blobs, timing | message content, group membership |
| Nickles | signed transactions | real identity linkage (pseudonymous addresses) |
| Updates | app version, coarse platform | identity, browsing |

Metadata is *minimised*, not eliminated. That sentence stays in the UI help text.

---

## 6. Delivery plan (full detail in `docs/11`)

| Phase | Name | Duration | Gate |
|-------|------|----------|------|
| 0 | Discovery, ADRs, threat model, standards register | 2 wk | **G0** decisions D1–D10 signed |
| 1 | Repo, CI (Win+Linux), SBOM, signing, analyzers | 2 wk | **G1** pipeline red/green proven |
| 2 | Crypto core, keystore (DPAPI/TPM + Secret Service), vectors, fuzz | 5 wk | **G2** ACVP + Wycheproof + fuzz clean |
| 3 | Shell: Avalonia chrome, 36 px rail, tabs, omnibox, settings | 6 wk | **G3** 60 fps + a11y on both OSes |
| 4 | `jttp://` scheme (both engines), KML/KSS, sanitizer, CSP | 5 wk | **G4** XSS suite = 0 bypasses |
| 5 | Registry, identity, certs, cookies, **instant claim** | 5 wk | **G5** tamper tests + claim < 90 s |
| 6 | Blob model + PIN unlock UX + per-blob visibility | 4 wk | **G6** sealed-at-rest proof |
| 7 | PNG vault, ratchet, stale/refresh flow | 3 wk | **G7** forensic test passes |
| 8 | Mail (compose, sync, contacts, export/import) | 5 wk | **G8** no plaintext on server |
| 9 | FreeChat (MLS, presence, dock) | 5 wk | **G9** FS/PCS test passes |
| 10 | Panic / wipe / duress / auto-lock | 2 wk | **G10** crypto-erase verified |
| 11 | Clearnet mini-browser (Win + Linux) | 4 wk | **G11** JS-off default + sandbox verified |
| 12 | PHP/MySQL service, ops, backups, monitoring | 4 wk | **G12** pentest + load test |
| 13 | Sentinel CVE watcher + advisory feed | 3 wk | **G13** S0 drill < 4 h |
| 14 | Home: LegendDX + Arcade | 5 wk | **G14** game sandbox holds |
| 15 | Plugins, market, Nickles plugin | 6 wk | **G15** sandbox escape suite clean |
| 16 | Ad network (Nickles only) | 3 wk | **G16** no-tracking proof |
| 17 | Nickles testnet | 3 wk | **G17** legal sign-off, no value claim |
| 18 | Hardening, external audit, beta, release | 6 wk+ | **G18** zero open Critical/High |

Sequential for one developer ≈ 15–17 months; two developers with phases 8/9 and 14/15 in parallel
≈ 10–12 months to public beta. See `docs/11` for the dependency graph and effort model.

**Current build status (2026-10-25).** The running registry slice is not just a client simulation:
`server/` is a PHP 8.2 + MariaDB service with migrations, transactional registration, and stored
signed snapshots/deltas; `tools/Kickl.RegistrySigner` holds the hybrid key and signs offline; and
`kickl registry sync` verifies a full snapshot and then signed deltas into the local mirror. It was
run end-to-end locally (`v0 -> v1 -> v2`) against XAMPP MySQL. Registration is still an operator
admin token, not an owner-signed request; head signatures, delta consistency proofs, and the
browser UI wiring remain open. Everything else in the delivery table is still phase work.

---

## 7. Top risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| K1 | Crypto design flaw (custom protocol) | Med | Critical | standards-only rule; external audit at G2 and G14; test vectors; no hand-rolled primitives |
| K2 | XSS via decrypted blob HTML | High | Critical | sanitize + CSP + `sandbox` iframe + no script on sealed surface; fuzz; bug bounty |
| K3 | Supply chain (NuGet, WebView2, PHP deps) | High | Critical | lockfiles, SBOM, signature verification, Dependabot + OSV, pinned runtime, signed updates |
| K4 | Metadata leaks contradict marketing | High | High | measure and publish; UI wording reviewed by security; no absolute claims |
| K5 | Scope explosion (18 phases, one dev) | High | High | gates, cut lines (§8), Nickles deferred if needed |
| K6 | PHP/MySQL compromise | Med | Critical | ciphertext-only server, least privilege, WAF, prepared statements, offsite backups, rotation |
| K7 | Legal exposure (currency, ads, "encrypted" claims, exports) | Med | High | testnet-only, counsel review, no absolute claims, crypto export notification |
| K8 | User coercion / forgotten PIN = permanent loss | Med | High | recovery phrase, documented no-backdoor policy, optional escrow explicitly off by default |
| K9 | WebView2/WebKitGTK runtime regression/CVE | Med | High | Evergreen + minimum-version floor + kill switch + Sentinel advisories |
| K10 | SSD "secure delete" illusion | High | Med | crypto-erase as the only claim; TRIM; document backups/shadows |
| K11 | Linux parity slips (engine, packaging, secrets) | High | High | engine abstraction from day one; parity contract tests; Flatpak first |
| K12 | Plugin sandbox escape | Med | Critical | out-of-process, OS sandbox, capability broker, no ambient keys, escape test suite |
| K13 | AI-drafted advisory is wrong or injected | Med | High | schema-only output, no tools, human approval, source citation, retraction policy |
| K14 | Ads/currency create legal or trust damage | Med | High | ads only from Nickles, no tracking, testnet-only, public moderation log |

## 8. Cut lines (what gets dropped first if time runs out)

1. Nickles beyond a local points demo (drop entirely from 1.0 if legal review is unresolved).
2. Steganographic LSB layer (keep the encrypted PNG container — it delivers the user value).
3. Contact "friends of friends" auto-suggest (keep manual vouch bundles).
4. Full-text local registry search (keep prefix search).
5. Per-site custom themes.
6. Cross-device sync of read state.

Never cut: hybrid PQ handshake, PIN-local-only, panic crypto-erase, sanitizer/CSP, signed updates,
audit gate.

## 9. Standards register (the "must comply" list)

WHATWG URL · RFC 3986/3987 · RFC 8446 (TLS 1.3) · RFC 9110–9114 (HTTP semantics/caching) ·
RFC 6265bis (cookies) · CSP Level 3 · RFC 9180 (HPKE) · RFC 9420 (MLS) · Signal Double Ratchet ·
FIPS 203/204/205 · RFC 8439 · RFC 8452 (GCM-SIV) · RFC 5869 (HKDF) · RFC 9106 (Argon2) ·
BIP-39 / BIP-350 (Bech32m) · RFC 8949 (CBOR) · RFC 9052 (COSE) · RFC 6962 (transparency logs) ·
RFC 9530 (Content-Digest) · OWASP ASVS 4.0 L2+ · WCAG 2.2 AA · CycloneDX 1.5 (SBOM) ·
RFC 9116 (security.txt) · ISO 8601 · RFC 3339.

---

## 10. Definition of Done (applies to every work item)

- [ ] Code reviewed by a second pair of eyes (or a documented solo-review checklist).
- [ ] Unit tests + at least one negative test; crypto changes carry a test vector.
- [ ] No new dependency without licence + CVE + maintenance check recorded in `docs/10` §7.
- [ ] Threat-model delta written if the change touches keys, parsing, network, or the DOM.
- [ ] Logs contain no plaintext, no keys, no addresses, no message bodies.
- [ ] UI string reviewed for over-claiming (no "unbreakable", "anonymous", "untraceable").
- [ ] Accessibility: keyboard reachable, focus visible, contrast ≥ 4.5:1, reduced-motion honoured.
- [ ] `CHECKLIST.md` updated.
