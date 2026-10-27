# 00 — Decision Log

Append-only. A decision is only changed by adding a new entry that supersedes the old one. Each entry
has: context, decision, consequences, and what it invalidates.

---

## ADR-001 — Platforms: Windows + Linux (supersedes "Windows-only")

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** The first draft targeted Windows only (WPF + WebView2). You confirmed Linux is a 1.0
target. WPF cannot run on Linux.

**Decision.** Use **Avalonia UI 11** for the shell, .NET 10 LTS for the core, and a **pluggable web
engine**:
- Windows: **WebView2** (Evergreen Chromium), as before.
- Linux: **WebKitGTK 6** (libwebkitgtk-6.0) first, with **CEF** as a fallback if feature parity fails.

**Consequences.**
- One XAML-ish UI codebase; no WinUI 3 / WPF-specific APIs in shared code.
- Window chrome is self-drawn on both platforms; Snap Layouts is a Windows-only nicety (Linux gets
  compositor-native snapping).
- Web engine abstraction (`Kickl.Surfaces.IWebSurface`) with two implementations; every surface
  feature (custom scheme, CSP, JS toggle, snapshot capture, script-disabled mode) must exist in both.
- Linux packaging: `.deb`, Flatpak (preferred for update cadence + sandbox), AppImage (portable).
- Linux secret storage: Secret Service (`libsecret`) + optional TPM 2.0 (`tpm2-tss`); fscrypt where
  available. No DPAPI.
- Security patching on Linux depends on the distro/Flatpak runtime for WebKitGTK; that is a **feature**
  (distro security teams) but means we must set a minimum WebKitGTK version and refuse to run older.
- Schedule +4–6 weeks total for parity and packaging.

**Invalidates:** "Why WPF" in `REPORT.md` §4, Windows-only scope in `docs/01`, MSIX-only packaging in
`docs/02` §10, Windows-only hardening in `docs/10` §6.

---

## ADR-002 — Canonical scheme is `jttp://`

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** The draft used `omni://`; you chose `jttp://`, and clarified: **"jttp:// is our customized
DNS"** and **"never used in place of http — http will open another browser to make queries"**.

**Decision.** `jttp://` is a **parallel namespace** resolved by the Kickl registry, not a wrapper
around HTTP and not a replacement for it.

- `jttp://name.tld/path` → sealed Kickl document, resolved **only** by the local registry mirror
  ("our DNS"). No system DNS lookup, no `http` translation, no traffic interception.
- `http://…` and `https://…` → **clearnet**, handled by the separate browser path: the system default
  browser by default, or Kickl's JS-locked mini-browser if the user chooses it.
- We never rewrite `http(s)://` into `jttp://`, and never the reverse. A clearnet URL never becomes a
  sealed page silently.
- Bare input: `name.custom` (a namespace that cannot exist on the public web) resolves via `jttp` with
  a visible `sealed` chip; `name.com` (a real TLD) opens clearnet. Explicit `jttp://` always wins for
  sealed resolution.
- Real-TLD names under `jttp` (e.g. `jttp://pinwheelexplained.com`) are allowed, but they require a
  registry record plus an optional `_kickl.<domain>` TXT proof-of-control before the `verified` badge
  is shown; without it the page is marked `unverified name`.

**Consequences.**
- Registered as a standard, secure custom scheme on both engines:
  - WebView2: `CoreWebView2CustomSchemeRegistration { TreatAsSecure = true, HasAuthorityComponent = true }`
  - WebKitGTK: `webkit_web_context_register_uri_scheme()` with a `WebKitURISchemeResponse` and a
    `WebKitSecurityManager` policy; registered as a secure scheme for secure-context APIs.
- "jttp" is a **namespace + scheme**, not a transport. TLS/Noise still carry the bytes.
- The registry is therefore a **name authority**; its availability affects sealed navigation only,
  never clearnet browsing.
- Naming: the protocol suite remains "Pinwheel"; the document language is KML; the style language is KSS.

**Invalidates:** the rewrite rule in the previous revision, the `.custom`-only grammar in `docs/05` §1,
and "clearnet in a modal only" as the sole path in `docs/06` §7.

---

## ADR-003 — Own registry at secupgrade.com

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** The draft allowed interop with the existing secupgrade Pinwheel or a new registry.

**Decision.** We operate our **own** registry and Pinwheel service at secupgrade.com. It is the
authoritative phonebook. The existing `/live` Pinwheel chat is **not** protocol-compatible; we borrow
the concept (rolling epochs, server relays opaque data) and replace the PIN-to-server design.

**Consequences.**
- You own PHP/MySQL deployment, patching, backups, uptime.
- The registry signing key must live offline/HSM; rotating it is a documented drill.
- Existing secupgrade users do not migrate automatically; an import tool for old chat handles is a
  nice-to-have, not a 1.0 blocker.
- The server is **ciphertext-only** for mail and relay; only registry/advisory/ad metadata is public.

**Invalidates:** "Interoperate read-only with secupgrade" default in `README` (old), `docs/04` §3.

---

## ADR-004 — Product name: Kickl Browser

**Date:** 2026-10-24 · **Status:** Provisional (trademark screen pending) · **Owner:** you

**Decision.** Use "Kickl Browser" in code, docs, and UI strings. Namespaces become `Kickl.*`.
Codename "PINWHEEL" stays for the network. "Nickles" stays for the test currency.

**Naming map (for anyone reading older notes):**

| Old | New |
|-----|-----|
| Omnibrowser / OMNIBROWSER | Kickl Browser / KICKL |
| `omni://` | `jttp://` |
| OMNI markup / OMNI-DOC | KML markup / KML-DOC |
| OSS styles | KSS (Kickl Style Sheets) |
| `Omni.*` assemblies | `Kickl.*` |
| `nk1…` address | `kb1…` |
| `.omni.png` vault file | `.kickl.png` |
| `omni` chunk in PNG | `kiKl` chunk |
| "OmniMail" | "KicklMail" |

**Consequences.** Signing certificate, installer IDs, update feed URLs, and the repo must use the new
name. A trademark search before beta is a gate item.

---

## ADR-005 — Nickles is an optional plugin, opened via its quote panel

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** "Nickles can be added to the browser as a downloadable plug to interact with in browser
with the qt. which the qt is opened that is."

**Decision.** Nickles is **not** in the core installer. It is a signed plugin (`kickl-nickles`) that
adds:
- a wallet panel,
- a **quote panel** (the "qt") shown when the plugin is opened or when a payment/quote is requested,
- an optional ad-network client (see ADR-008).

The core exposes a narrow **payments/quote capability** to plugins; it never exposes keys or the vault.

**Open question:** whether "qt" means "quote" or the Qt toolkit. We implement **quote panel**; if you
meant Qt, tell us before Phase 15. This is recorded as Q-03.

**Consequences.** Nickles can be omitted by users who do not want the legal/economic surface. The
plugin must pass the same sandbox and signing rules as any third-party plugin. The testnet-only rule
(`docs/09`) applies.

---

## ADR-006 — Instant claim + build for unknown URLs

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** "Registering for sites should be easy like inputting an unknown url should make the
prompt come up to take it and build for it in the moment."

**Decision.** Navigating to an unregistered `*.custom` name shows a **Claim sheet**:
1. shows availability, first-seen, and a "did you mean" list (typo protection),
2. one click to generate a domain key (or reuse identity), choose relay endpoint, solve a small PoW,
3. publish the record to the registry,
4. immediately open the **builder** with a starter KML page, a live preview, and "publish v1".

The whole flow must work in **under 90 seconds** on a fresh install, offline-tolerant (queue the
publish if the registry is unreachable).

**Consequences.** New abuse surface: squatting, typo-squatting, phishing pages. Controls: PoW,
3 claims/day/device, reserved names, a 7-day "new site" badge in the omnibox, similarity warnings, and
an abuse-report button. Claims are logged in the transparency log.

**Invalidates:** `docs/05` §8 (registration UX) — extended, not replaced.

---

## ADR-007 — Home page: half LegendDX, half user-made arcade

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** "The index page of the whole thing is half a search engine called legenddx on
secupgrade.com and the other half games made by users of the browser making fame instantaneously for
some."

**Decision.** The new-tab/home surface is a 50/50 split with the **games array first** and the
**LegendDX search second**:
- **Arcade pane (first half)** — an array of user-made games, playable in a sandboxed runtime, with
  plays, likes, remix lineage, and a weekly leaderboard. "Fame" = visibility + profile stats, not
  money. The browser-first slice ships a native tic-tac-toe demo and queues wasm-1 bundles.
- **LegendDX pane (second half)** — local registry search over the PFS channel, by the tags the
  person sends; an explicit "search the open web" action hands the query to LegendDX in the
  clearnet modal (visible, opt-in, no silent query leak).

**Consequences.** The sealed surface is script-free, so games need a **separate sanctioned runtime**
(WASM + a tiny JS API inside a sandboxed WebView2/WebKit view with no network, no clipboard, no
camera, no filesystem). This is a new attack surface with its own threat model (Phase 14).
Games are published as KML documents with a `game` block referencing a signed bundle, not as raw
clearnet pages.

**Invalidates:** the "search bar + viewport only" home assumption in `docs/06`; adds `docs/13`.

---

## ADR-008 — Advertising only through the Nickles ad network; first slot in the home footer

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Decision.**
- No third-party ad script, pixel, or network is allowed anywhere in the app.
- Ads appear **only** on Kickl-owned surfaces (home, arcade, plugin catalog, Nickles panel), never
  injected over a website, never in mail/chat, never on the sealed document canvas.
- The first placement is the **full-width home footer bar**, spanning the browser pane above the
  status bar, always labelled `AD`. It is part of layout, never an overlay over content; it
  collapses to a labelled strip.
- Placement order is a public **donation-weighted list**: rank = f(donation amount, recency decay,
  quality score). Paying more buys a larger share of the rotation, **not** targeting.
- Creatives are static images/text served as content-addressed blobs. No tracking, no impression
  beacons, no cookies, no geo, no behavioural targeting. Rotation is time-based and identical for
  everyone in a region-free pool.
- Payment in BTC (direct, non-custodial invoice) or NKL (testnet in 1.0).

**Consequences.** We cannot report impressions to advertisers (by design); we report **rotation share**
and **slot-time purchased**. Moderation is manual with a published policy. Ads are dismissible to a
one-line strip; the slot never covers content and never animates more than a 200 ms fade.

**Invalidates:** none, but adds `docs/15` and server tables/endpoints.

---

## ADR-009 — Plugins may be sold for BTC, NKL, or free

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Decision.** Plugin catalog supports three price modes: `free`, `btc`, `nkl`. Payments are
non-custodial invoices (unique address per order, or a Lightning invoice later). Entitlement is a
signed license token bound to the buyer's `kb1…` identity, stored locally and re-verifiable offline.
Plugins are always **signed by the author and countersigned by the catalog**, sandboxed, and
capability-scoped.

**Consequences.** Tax/consumer-law obligations for paid plugins; refund policy required; we do not
hold funds (direct-to-seller) in 1.0, which keeps us out of custody but complicates disputes. Revenue
split is an open question (Q-04).

---

## ADR-010 — Sentinel: AI-assisted CVE watcher

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** "An ai will get that does for us reading cves by downloading them on a timer and then
alerting the pinwheel server/secupgrade.com with a summary of what to do to protect the computer."

**Decision.** Build **Sentinel**, a server-side worker:
- polls NVD/OSV/MSRC/vendor feeds on a timer (default 30 min),
- normalises and dedupes advisories,
- matches them against our SBOM and the installed/runtime inventory,
- uses an LLM **only to draft** a plain-language impact + remediation summary, under a strict JSON
  schema, with the CVE text treated as untrusted data,
- opens a **human approval queue**; only approved advisories are published to clients,
- publishes signed advisories to `/v1/advisories`; the client shows a shield badge and can apply
  client-side mitigations (feature kill switch, minimum-version floor).

**Consequences.** New server component + tables + a signed advisory feed. The LLM never gets
credentials, never auto-patches, and never executes instructions found in CVE text. False positives
are expected; the approval queue is the control.

**Invalidates:** nothing; extends `docs/10` §5 and adds `docs/12`.

---

## ADR-011 — Input privacy, per-identity data, and the session gate

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** You added: ban mouse tracking and keystroke plaintext leakage; devices must not be
queryable; AES-256 per keystroke using a secret made with the browser session; every person gets their
own entire browser data; first use asks "signed in" or "not registered" and requires a user key to
enter the session.

**Decision.**
1. **Entry gate.** On first run (and every cold start unless "stay unlocked" is chosen) Kickl asks
   `Sign in` or `Not registered`. Both require a **user key**: PIN/passphrase + optional hardware
   authenticator (FIDO2) or TPM-bound key. The user key derives the session; it is never stored and
   never sent anywhere.
2. **Session secret.** On unlock, derive `sessionKey = HKDF(vaultMaster, salt=sessionNonce,
   info="kickl/v1/session")`. It lives only in pinned, zeroised memory and dies on lock.
3. **Per-keystroke AES-256-GCM.** Sensitive input (omnibox, PIN, compose, search) is held as a sequence
   of individually sealed cells: `ct_i = AES-256-GCM(sessionKey, nonce=counter_i,
   pt=char_i, aad=sessionId|fieldId|counter_i)`. Text is materialised only for the frame that paints
   it, into a buffer that is zeroised immediately. No keystroke reaches a log, crash dump, history,
   clipboard, IME learner, or telemetry sink.
4. **Mouse tracking ban.** Pointer position is used for hover/paint only, never stored, never logged,
   never sent. No heatmaps, no analytics, no "where did you click" records. Sealed documents have no
   script, so they cannot listen at all.
5. **Device-query ban.** No hardware IDs, serials, MACs, battery, font lists, sensor reads, or device
   names are collected. The server schema has no device-fingerprint columns. The clearnet mini-browser
   enables the engine's fingerprinting protections and uses a per-session randomized profile; sealed
   documents cannot query the device because they cannot run script.
6. **Per-person data.** Each identity gets its own encrypted profile root
   (`profiles/<identityId>/…`: tabs, history, cookies, vault index, mail, chat, keys). Switching
   identity is a full profile swap; one identity cannot read another's state. The public registry
   snapshot is the only shared cache.

**Consequences.**
- Per-keystroke AEAD costs ~1–2 µs per character on desktop; acceptable, but it is applied only to
  sensitive fields, not to a game canvas or a clearnet page.
- Plaintext still exists in the paint buffer and on screen for one frame. The threat model says so
  plainly: this defeats memory scrapers and careless logging, not a kernel-level keylogger or a camera.
- Accessibility must keep working: screen readers need the composed value. Sensitive fields expose it
  only through the platform accessibility API on explicit focus, and we document that.
- "Stay unlocked" is opt-in, time-boxed (max 12 h), and re-prompts on OS lock.
- Adds `docs/16-input-privacy-and-sessions.md` and Phase 2/3 checkpoint items.

---

## ADR-012 — Paid ownership titles: RSA-wrapped AES token, badge, sale and trade

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** You added a paid ownership verification: a USB-delivered private key plus the site's
public key, sent to secupgrade.com, which issues an ownership badge. The key opens an owner account
area that can sell or trade the name. Without a title, a name can only be created or closed by its
user. The key opens a connection to the owner, and the token sent over that tunnel makes ownership
real.

**Decision.**
1. **USB bundle.** The USB holds an **RSA-3072** private key and the **AES-256 ownership token
   wrapped with RSA-OAEP-SHA256**. The token is never stored unwrapped, never in the profile
   directory, and never in a backup. The public record is `{site, RSA public SPKI, token
   commitment, ownerId}`.
2. **Why RSA + AES.** AES is a symmetric cipher; it has no public key and cannot sign. RSA covers
   (encrypts) the AES token at rest and signs ownership statements. This matches the intent: the
   USB's RSA private key unwraps the AES secret, and the AES secret is the token presented to the
   registry.
3. **Verification ceremony.** secupgrade.com sends a 32-byte challenge → the USB unwraps the token,
   signs the challenge with RSA-PSS, and sends the token through the ownership tunnel (HPKE/Noise in
   production, a local stand-in in the slice) → the registry checks the token commitment and
   signature → it issues a **badge** (`kb1b…`) tied to `ownerId` and title sequence `seq`.
4. **Badge lifetime.** Active for one year, refreshed by the owner; revoked on transfer, closure, or
   expiry (30-day grace). The badge is displayed on the sealed page and in the owner area.
5. **Owner account area.** Unlocked by the token. It lists the title, renewal date, buyer offers, and
   exposes **List / Sell / Transfer**. Payment rails are BTC, NKL, or free; the slice simulates
   payment and does **not** claim a paid tier is live.
6. **Transfer.** The seller signs a transfer statement (site, from/to owner ids, buyer RSA key, buyer
   token commitment, seq, price). The buyer imports the statement and proves possession of their
   own key; the registry advances `seq`, issues a new badge, and the seller's key can no longer sign.
7. **No title, no trade.** Names without a title can be created (publish) and closed by their user,
   but cannot be sold, traded, or given a badge.
8. **Issuer signature.** The slice records `IssuerSig = "dev-unsigned"`; a real registry-signed
   title (Ed25519/ML-DSA over the title record) is a G5 requirement, not a claim.

**Consequences.**
- Losing the USB freezes the title until the recovery flow (recovery phrase + registry support) is
  exercised; the slice has no recovery.
- The registry learns the AES token during verification. A later revision should accept a derived
  proof (HMAC/AES-GCM challenge) so the raw token never leaves the client; until then the tunnel is
  the only boundary and that is stated in `docs/17`.
- The title record is separate from the DNS/TXT proof: `verified name` and `paid owner` are two
  different badges and the UI must not conflate them.
- Adds `docs/17-ownership-titles.md`, `Kickl.Core/Ownership`, and Phase 5 checklist items.

---

## ADR-013 — Per-region sealed pages, stego cache, and polymorphic re-seal

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** You specified that sites are made of encrypted blobs, self-encrypted for screen/eye
privacy; each clicked part decrypts to a blob; the whole site image is saved as a JPG for
steganographic storage in a sovereign certificate only the in-session user knows; a seeded
"polymorphic" step changes the ciphertext each time while the plaintext stays the same; the
steganography is refreshed on every cache request and decrypted with the saved encrypted material
under the user's private key.

**Decision.**
1. **Regions.** Page text is split into independently sealed regions (`SealedPage`), each
   AES-256-GCM under a per-page key derived from the session secret. The renderer shows placeholders
   until a region is clicked; only that region's plaintext is materialised, and it is dropped on
   dispose/lock. Headings, paragraphs, code, and quotes are regions; images/files are already blobs.
2. **Lossless carrier, not JPEG.** The cache envelope is embedded with keyed LSB steganography in a
   **PNG**. JPEG's lossy transform destroys LSB payloads; a JPEG carrier would require DCT-domain
   embedding, which is explicitly deferred. The file stays the `.kickl.png` vault format.
3. **Session-bound.** The permutation key and payload key are HKDF(SHA-256) outputs of the live
   session secret, salted by a fresh 32-byte seed. The seed is in a `tEXt` chunk; without the session
   secret the carrier is just an image. Nothing in the profile path names the site.
4. **Polymorphic re-seal.** Every cache write/request generates a new seed, new payload key, new
   nonce, new carrier pattern, and rewrites the same file. The ciphertext and the image bytes change;
   the decrypted envelope is identical. A nonce or seed is never reused with the same key.
5. **Cache refresh on read.** `Load(refresh: true)` (the default) extracts the envelope and
   immediately re-seals it. This is the "steganography is updated each time the cache is requested"
   requirement.
6. **Honest limits.** Steganography is concealment, not a cryptographic boundary; a statistical
   analysis can suspect LSB payloads. Screen pixels still exist in the compositor and on the panel;
   OS capture exclusion helps only where the platform supports it; a camera is out of scope. These
   limits go in the doc and the UI.

**Consequences.**
- The cache is not portable between sessions/identities by design; re-wrapping for the vault key is
  a G7 task.
- Cache files grow to PNG size (hundreds of KB in the slice); the real shell will render smaller
  tiles or use a compressed carrier.
- Adds `docs/18-sealed-pages-and-stego-cache.md`, `Kickl.Core/Privacy/SealedPage.cs`,
  `Kickl.Core/Stego/*`, tests, and Phase 6/7 checklist items.

---

## ADR-014 — PFS on every Kickl link, temporal keys, and the tagged live index

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** You asked whether every connection to another computer is PFS-encrypted like the
LegendDX lookup, and said temporariness is the point. You also specified a live index at
`secupgrade.com/legenddxI2` where every live site returns a title, headline, timestamp, and domain;
clicking a result must prompt the device; every site must carry tags for the phonebook search; the
public/private key should be refreshed per browser opening.

**Decision.**
1. **PFS for every Kickl link.** Registry name lookups and live-index searches use a fresh P-256
   ECDH pair per session, HKDF-SHA384, and AES-256-GCM, then destroy the session key. Device mesh
   links use a fresh P-256 ECDH pair per link, HKDF-SHA256 into two directional AES-256-GCM keys,
   and RSA-3072 only to sign the offer/accept. RSA key transport is removed; it had no forward
   secrecy. Clearnet stays a separate lane.
2. **Temporal key layers.** Stable: ownership RSA key, device authentication key, PIN-wrapped master.
   Per opening: session secret, session P-256 identity, region/stego/keystroke subkeys. Per
   connection: ECDH keys, HKDF outputs, nonces, counters. Everything below the master is destroyed on
   lock/close and never written to disk. The ownership key must not rotate, or titles break.
3. **Tagged live index.** A publish requires `<meta name="headline">` and 1–8 valid tags
   (`a–z0–9-`, 2–24 chars). The registry stores title, headline, tags, and a server-set `updatedAt`.
   Search matches tags first, then name/title/headline, excludes closed names, and returns at most 50
   results. The query and response travel inside PFS frames.
4. **Content commitment.** The publisher sends SHA-256 of the package (name + markup + sealed blob
   ciphertexts). The receiver recomputes it before parsing or rendering. A mismatch fails closed and
   increments the record's attack counter; the slice increments the local mirror, production queues
   an anonymous `/registry/mismatch` report. This is transport integrity, not authorship.
5. **Explicit activation.** Results are inert; the device shows a confirmation sheet naming the host,
   title, tags, and owner badge. Only confirmation calls `Open`. No prefetch, no hover fetch, no
   click telemetry.
6. **Top screen.** Touch RSS scroller with a stylus-summoned mini search overlay. It runs the same
   PFS index query but hands off to the inner screen for confirmation and opening. It never shows
   blobs, mail, chat, or keys.
7. **Honest scope.** The slice stores markup as public structure; the live index is publish-time, not
   a crawler; `/legenddxI2` deployment and the top-screen UI are not built yet. Those stay unticked.

**Consequences.**
- New `Kickl.Core/Registry/Phonebook.cs`, tagged `RegistryRecord`, PFS search framing,
  `MeshLink` rewritten to ECDH, `SessionSecret` session identity, `search` CLI, and tests.
- Docs 20–21 and API/schema reference updated; title rotation is explicitly forbidden.

---

## ADR-015 — Inline SVG/HTML instead of a CSS dialect for sealed visuals

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** You asked for a div filled with inline SVG/HTML as a normal programming choice, and
specified "instead of CSS we use HTML inline SVG." The original KML plan had a separate KSS/OSS style
language and forbade SVG entirely.

**Decision.**
1. A sealed page may contain an `<html>` (alias `<visual>`) block whose text is an HTML fragment. The
   intended visual idiom is a `<div>` containing inline `<svg>`; CSS is not a required authoring
   language.
2. The fragment is sanitized before it enters the page model. Script, stylesheet elements, forms,
   inputs, frames, objects, external URLs, event attributes, `style` attributes on HTML, and
   `foreignObject` are removed. Inline SVG keeps static shapes, gradients, text, paths, `use`,
   raster data images, and internal `#id` references.
3. The renderer is deliberately not a browser engine: it maps containers, text, lists, tables,
   data-URL raster images, and inline SVG to native Avalonia controls. It will not run script, load
   remote resources, submit forms, or execute CSS. Unsupported markup is dropped or shown as inert
   text.
4. Free inline visuals belong to `.com` and custom pages. A new custom name starts with a complete
   editable example page (`SiteStarter.Example`) and a live preview in the editor, so the author
   learns the format by modifying a working page.
5. `jttp://name.rss` keeps its fixed story template — image, headline, author, date, location,
   story, tags — and does **not** accept inline HTML/SVG. Its authoring surface is an XML
   `<rss><channel><item>` template; publishing parses the item into those fields. RSS is a feed
   format, not a free page.
6. Raster data-URL images inside a free inline fragment are extracted at publish time and sealed as
   ordinary document blobs under AES-256-GCM. The markup keeps only a public
   `kickl-blob:<blob-id>` reference. Clicking the unlock control decrypts that blob in the session
   and re-renders the SVG; the sealed KML never carries the original data URL.
7. Limits: 96 KiB of inline fragment, bounded element depth and document size, and SVG rendering
   capped to a 760×560 viewport.

**Why.** Inline SVG gives authors layout and drawing without a CSS parser or a complex cascade. It
also keeps the security story simple: static drawing commands, no executable content, no network.

**Consequences.** KSS/OSS is deferred rather than required for the current slice. `docs/07` changes
from "SVG forbidden" to "inline SVG allowed through the sanitized visual block." New dependencies:
`HtmlAgilityPack` (parsing/allowlist) and `Avalonia.Svg.Skia` (static SVG rasterization). Both need
the Phase 1 licence/CVE review recorded before release.

**Implementation note (2026-10-25).** The browser now routes sealed fragments through an
`IWebSurfaceEngine` boundary. Its current Avalonia implementation remains a native static renderer;
`SealedSurfacePolicy` provides the shared `jttp` navigation and no-network defaults for any future
platform adapter. The CSP string is a policy contract for a future WebView adapter, not an active
browser-enforced header today. The current renderer enforces the same intent by not providing a
script engine or URL loader and by sanitizing again at its rendering edge.

---

## ADR-016 — NCKL publisher opt-in for webmasters

**Date:** 2026-10-24 · **Status:** Accepted · **Owner:** you

**Context.** You specified that webmasters have a choice to use NCKL ads by inserting the ad into
their ad space after going to `secupgrade.com/adnetworking`, opening the publisher, getting the JS
file, and marrying it to the site. ADR-007 and the original ad-network design limited ads to
Kickl-owned surfaces and forbade scripts.

**Decision.**
1. The sealed-site surface now has an **opt-in publisher path**. A webmaster opens
   `jttp://secupgrade.com/adnetworking/publisher`, enters the jttp host they control, and mints a
   placement for one ad space (`site.inline` in the slice).
2. Minting returns a publisher id, a site-bound token, `nickles.js`, and a pasteable snippet
   containing an ad-space `<div>` and the official loader `<script>`.
3. The loader is a **signed placement manifest, not an executed script**. Sealed pages still have no
   script engine and make no external requests. At publish, `SiteComposer` verifies the loader's
   signature against the host, the space, and the local publisher store; a mismatch fails closed. It
   replaces the snippet with one KML `<ad publisher site space token>` block.
4. At render, the browser resolves a deterministic 10-minute creative from the signed local catalog.
   The `AD` label is always visible. A token copied to another site does not validate.
5. The site's owner still chooses whether to include the snippet. Ads are never injected into a
   page that did not opt in. No impression, click, cookie, device id, geo, or behavioural data is
   recorded or transmitted.
6. RSS stories cannot carry an ad block. The fixed story template remains unchanged.

**Why.** This preserves the security model — no third-party JS, no network loader, no tracking — while
giving webmasters a real, explicit opt-in and a shareable loader file. The site binding is
cryptographic, so a copied snippet fails rather than monetising someone else's page.

**Consequences.** `docs/15` now describes publisher slots as well as the home slot. The slice uses a
built-in signed creative catalog; production still needs a signed slot endpoint, review queue, and
rotation audit. `nickles.js` must be documented as a manifest to avoid implying a JS runtime exists.

## 1. Superseded assumptions

| Assumption in v1.0 of the report | Now |
|----------------------------------|-----|
| Windows only, WPF, WebView2 | Windows + Linux, Avalonia, WebView2 or WebKitGTK/CEF |
| `omni://` | `jttp://` |
| Registry optional/interop | Own registry, authoritative |
| Nickles in core | Nickles plugin |
| Home = blank/new tab | Home = games array + LegendDX PFS search split |
| No ads, ever | NCKL only: home slot + opt-in, site-bound publisher slots; no third-party ad script |
| CVE process is manual | Sentinel drafts, human approves |

## 2. Open questions (blocking at the phase noted)

| ID | Question | Blocks | Default if unanswered |
|----|----------|--------|-----------------------|
| Q-01 | Audit budget (~USD 25–60k) approved? | G2, G18 | No release |
| Q-02 | Linux packaging priority: Flatpak vs .deb vs AppImage | Phase 3 | Flatpak + .deb, AppImage best-effort |
| Q-03 | Does "qt" mean **quote panel** or the **Qt toolkit**? | Phase 15 | Quote panel |
| Q-04 | Paid-plugin revenue split and who handles refunds | Phase 15 | 100% to seller, no platform fee in 1.0 |
| Q-05 | May LegendDX queries leave the device? | Phase 14 | Only after an explicit click, in the clearnet modal |
| Q-06 | Is Nickles ever allowed to become real money? | Phase 17 | No (testnet forever in 1.x) |
| Q-07 | Who moderates ads and games, and what is the SLA? | Phase 14/16 | You, 72 h best effort |
| Q-08 | Do we accept ARM64 Linux builds? | Phase 3 | x64 only for 1.0; arm64 best-effort |
| Q-09 | Legal entity + terms/privacy owner | G18 | Required before public beta |
| Q-10 | WebKitGTK vs CEF as the Linux default | Phase 0 spike | WebKitGTK if parity ≥ 90%, else CEF |

## 3. Review checklist for the next pass

- [ ] Confirm the naming map and that "Kickl" is the intended spelling (not "Kickle").
- [ ] Confirm `jttp://` capitalization in UI (`jttp://` lower-case).
- [ ] Confirm home split behaves on narrow windows (stacked, not squeezed).
- [x] Confirm the ad slot position: full-width home footer bar above the status bar (changed from the lower-left card).
- [ ] Confirm donation ranking formula and whether it is public.
- [ ] Confirm Nickles plugin boundary (what the core exposes).
- [ ] Confirm Sentinel approval flow (who clicks "publish").
- [ ] Confirm Linux distro support matrix.


## Decisions of 2026-10-27 (security hardening)

| ID | Decision | Why |
|----|----------|-----|
| D-S1 | **Direct mail is TLS 1.3 with a key pin carried in the signed endpoint** (`https://host:port#pin=...`); plain http or an unpinned endpoint is refused, never downgraded to. | A LAN address has no certificate authority; the identity signature already authenticates the endpoint string, so the pin is authenticated for free and no new trust root is needed. |
| D-S2 | **The browser trusts exactly one CA, `https://secupgrade.com/secca/`.** Test overrides exist only in Debug builds. | One audited trust path; a shipped build cannot be pointed elsewhere by an environment variable. |
| D-S3 | **Owner binding:** the buyer's passphrase is turned into an HMAC (never sent), signed into the receipt by the shop, and sealed by the CA. | A `.kcert` file alone should not be enough to edit a name; binding at purchase removes the "whoever seals first decides" gap. |
| D-S4 | **FreeChat stays compatible with the existing service;** authentication is added client-side (pinned member keys, signed messages in a PNG trailer). | The service and its web client keep working; the remaining gap (server decides membership) is documented, not hidden. |
| D-S5 | **CA and shop signing keys are DPAPI-wrapped on Windows;** key files are ACL-restricted on every platform. | Offline copies of the files are not usable; the key still works for the account that runs the server. |
