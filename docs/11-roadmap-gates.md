# 11 — Roadmap, Gates, and Execution

## 1. Shape of the work

Build a **vertical slice first**: launch → identity → open `jttp://pinwheelexplained.com` → unlock one
blob → lock. That path exercises every layer (shell, crypto, registry, parser, IPC) and proves the
architecture before breadth. Mail, chat, home, plugins, ads, and Nickles come after the slice is
stable.

```
0 ─ 1 ─ 2 ─┬─ 3 ─ 4 ─ 5 ─ 6 ─ 7 ─┬─ 10 ─┬─ 18 ─ 19
           │                       │      │
           ├─ 8 ─ 9 ───────────────┘      │
           ├─ 12 ─ 13 ────────────────────┤
           ├─ 14 ─ 15 ─ 16 ─ 17 ──────────┘
           └─ 11 ─────────────────────────┘
```

## 2. Effort model

Assumes **1 senior C# engineer + 0.5 designer + 0.25 security reviewer + 0.25 backend** (or 2
full-stack engineers). Dev-weeks include tests and docs.

| Phase | Name | Dev-weeks | Parallel with |
|-------|------|-----------|---------------|
| 0 | Decisions & discovery | 2 | — |
| 1 | Engineering baseline | 2 | 0 |
| 2 | Crypto core & keystore | 5 | 1 |
| 3 | Shell (Avalonia, Win+Linux) | 6 | 2 |
| 4 | Sealed surface (`jttp`, KML, KSS) | 5 | 3 |
| 5 | Registry, identity, instant claim, cookies | 5 | 4 |
| 6 | Blobs & unlock | 4 | 5 |
| 7 | Vault & ratchet | 3 | 6 |
| 8 | Mail | 5 | 7 |
| 9 | FreeChat | 5 | 8 |
| 10 | Panic / wipe | 2 | 9 |
| 11 | Clearnet modal (Win+Linux) | 4 | 9 |
| 12 | Server, ops, registry hosting | 4 | 5–11 |
| 13 | Sentinel CVE watcher | 3 | 12 |
| 14 | Home: LegendDX + Arcade | 5 | 13 |
| 15 | Plugins, market, Nickles plugin | 6 | 14 |
| 16 | Ad network | 3 | 15 |
| 17 | Nickles testnet | 3 | 16 (optional) |
| 18 | Hardening, audit, beta, release | 6+ | — |
| 19 | Handheld, PFS links, tagged live index | 3 | 12, 14 |
| **Total** | | **≈ 76–83** | **≈ 16–18 months at 1 FTE** |

With 2 engineers, phases 8/9 and 14/15 can run in parallel; realistic wall-clock ≈ 10–12 months to
public beta.

## 3. Phase detail

### Phase 0 — Decisions (2 wk) → **G0**
Lock D1–D10 (`docs/00-decisions.md`), threat model v1, standards register, naming/trademark screen,
audit budget, Linux packaging choice, engine choice spike. Exit: no open question blocks Phase 1.

### Phase 1 — Baseline (2 wk) → **G1**
Solution skeleton, analyzers, CI on Windows + Linux, SBOM, secret scan, signing, MSIX + Flatpak, safe
logger, `deps.json` policy, gate notes. Exit: a signed "hello world" installs on a clean Windows 11 VM
and an Ubuntu 24.04 VM; CI blocks a deliberately vulnerable commit.

### Phase 2 — Crypto core (5 wk) → **G2**
AEAD trio, HKDF, Argon2id, hybrid KEM/sig, HPKE, Noise, SecureBuffer, keystore (DPAPI-NG/TPM on
Windows; Secret Service + TPM2 on Linux), recovery phrase, device certs, vectors, fuzz, timing tests,
external design review. Exit: all vectors green; 24 h fuzz clean; no Critical/High from review.

### Phase 3 — Shell (6 wk) → **G3**
Avalonia app, custom chrome on both OSes, 36 px rail, tabs-in-rail, omnibox, local suggestions, snap,
themes, `LightButton`, dock shell, settings, keyboard, accessibility. Exit: 60 fps; 200% zoom; screen
reader smoke; Linux window controls behave under GNOME and KDE.

**Implementation status (2026-10-25): partial.** The Avalonia browser, local profile gate, basic tab
management, omnibox navigation, and several keyboard shortcuts are present. Full profile-aware tab
state, omnibox suggestions, settings, accessibility verification, and platform-native window chrome
remain open work.

### Phase 4 — Sealed surface (5 wk) → **G4**
`IWebSurface` abstraction; WebView2 and WebKitGTK implementations; `jttp://` registration as secure
standard scheme; KML parser; KSS compiler; sanitizer; CSP; JS off; network kill switch; sealed
placeholders; 500-payload corpus; parser/SSE fuzz. Exit: zero bypasses, zero outbound requests.

### Phase 5 — Registry & identity (5 wk) → **G5**
`kb1…` addresses, short codes, safety numbers, snapshot/delta/Merkle, TOFU pins, local resolution,
**instant claim sheet**, typo protection, starter builder, domain cert view, cookie jar, inspector.
Exit: tamper/replay/rollback/equivocation tests; 100 random navigations produce zero packets; a new
name can be claimed and published in < 90 s.
*Slice status (2026-10-25):* snapshot/delta sync, hybrid verification, Merkle root, rollback and
equivocation detection, TOFU pins, the PHP/MariaDB registry server, the offline signer, and
`kickl registry sync` are in place. Zero-packet navigation, the in-app claim sheet, cookie jar,
local resolution inspector, and head-signature/consistency-proof handling are still open.

### Phase 6 — Blobs (4 wk) → **G6**
Document envelope, per-blob wrap, PIN gate, session cache, per-blob state, audit, failure paths.
Exit: sealed-at-rest grep test; tamper fails closed; unlock < 250 ms.

### Phase 7 — Vault (3 wk) → **G7**
Capture (CDP on Windows, WebKit snapshot on Linux), redaction, `.kickl.png` container, thumbnail, LSB
manifest, ratchet, gallery/viewer, refresh-from-owner, export. Exit: container fuzz + torn-write
recovery; forensic test; 200 items at 60 fps.

### Phase 8 — Mail (5 wk) → **G8**
Store, compose, sealed sender, Bcc envelopes, outbox, attachments, contacts/bundles/vouches, Requests,
blind-index search, receipts. Exit: packet capture + DB dump show no plaintext; idempotent delivery;
100-message soak.

### Phase 9 — FreeChat (5 wk) → **G9**
X3DH + Double Ratchet, MLS groups, presence, typing, TTL, direct/relay fallback, dock panel,
notifications. Exit: FS/PCS + out-of-order/loss/replay; 20-member churn; 8 h soak.

### Phase 10 — Panic (2 wk) → **G10**
Lock, scoped wipe, crypto-erase order, profiles, logs, auto-lock, duress (optional), known-copies.
Exit: erase verified on VM + physical SSD; no key material in dumps/logs.

### Phase 11 — Clearnet (4 wk) → **G11**
Disposable profile on both engines, JS-off default, per-site consent, cookie blocking, downloads
quarantine, permissions, amber signalling, expand/close, profile destruction. Exit: JS cannot run
without consent; profile gone after close; no `file://` escape.

### Phase 12 — Server & ops (4 wk) → **G12**
*Slice status (2026-10-25):* a registry-subset server exists (`server/`, PHP 8.2 + MariaDB):
migrations, prepared statements, signed snapshot/delta storage, operator-only registration, and a
local end-to-end sync. Ops items (nginx/TLS, rate limits, nonce replay cache, mail/relay/blob
routes, backups, monitoring, kill switch, runbook) are not started.
PHP/MySQL reference service, Merkle log, mail/relay/blob, GC, rate limits, nginx, backups, monitoring,
runbook, load test, DAST. Exit: restore < 1 h; 10× peak; pentest closed; kill switch tested.

### Phase 13 — Sentinel (3 wk) → **G13**
Feed workers, inventory matching, LLM drafter with schema + injection guard, approval queue, signed
advisory feed, client badge + remediation page, alerting. Exit: S0 simulated end-to-end in < 4 h.

### Phase 14 — Home (5 wk) → **G14**
Split layout, LegendDX local search, explicit open-web handoff, arcade feed, game bundle + sandbox,
arcade API, leaderboards/fame, publish flow, starter pack, moderation. Exit: a malicious game cannot
read keys, reach the network, or crash the browser; 3 starter games playable offline.

### Phase 15 — Plugins & market (6 wk) → **G15**
Manifest, package, signatures, capability broker, AppContainer/bubblewrap sandboxes, WASM host, KML
panels, install/update/revoke, catalog, reviews, BTC/NKL invoices, license tokens, **Nickles plugin
with the quote panel**. Exit: sandbox escape suite clean; a paid plugin verifies offline; Nickles
plugin cannot see keys.

### Phase 16 — Ad network (3 wk) → **G16**
Slot, label, dismiss, no-tracking proof, public ranking, deterministic rotation, creative pipeline,
moderation, payments, signed responses, takedown log. Exit: packet capture shows no impression/click
beacon; ranking reproducible from public data.

### Phase 17 — Nickles testnet (3 wk, optional) → **G17**
Chain, tx, wallet, faucet, explorer, banners, gates, legal. Exit: legal sign-off; release cannot enable
mainnet behaviour.

### Phase 18 — Hardening & release (6+ wk) → **G18**
Audit, pentest, bounty, red team, soak, wipe re-test, update/rollback, accessibility, i18n, help,
policies, support, launch. Exit: zero open Critical/High; all gates re-verified.

## Current completion checklist: Vault, communications, wipe, clearnet, Nickles, ads

Audit date: 2026-10-25. A checked item means the code has that slice; it does not mean the phase
passed its release gate. Do not mark a phase complete from these foundation items alone.

### Vault and sealed saves (G7)

- [x] Per-blob encrypted storage and explicit in-session unlock for sealed-page content.
- [x] Session lock clears opened blob/text/image state from the browser UI.
- [x] PNG steganographic carrier/cache primitives exist in `Kickl.Core.Stego`.
- [ ] Capture sealed page visuals across Windows and Linux, including redaction and thumbnails.
- [ ] Complete `.kickl.png` container UI: create/import, vault gallery, inspect, export, delete.
- [ ] Add vault key wrapping/ratchet lifecycle, refresh-from-owner, and torn-write recovery.
- [ ] Verify ciphertext-at-rest, forensic recovery limits, and cache/key wiping on supported OSes.

### Mail and FreeChat (G8–G9)

- [ ] Implement encrypted mail stores, compose/read UI, delivery/outbox, contacts, and attachments.
- [ ] Implement sealed sender, Bcc envelopes, blind index, receipts, and replay/idempotency handling.
- [ ] Implement X3DH/Double Ratchet direct chat, MLS groups, presence, TTL, and relay fallback.
- [ ] Add chat/mail panels, notifications, block/report controls, and key/device enrollment UX.
- [ ] Verify packet captures and database dumps contain no plaintext; run replay/loss/churn soaks.

### Panic and wipe (G10)

- [x] Manual lock evicts the live session and opened blob/image/text state.
- [ ] Add scoped wipe for session, mail/chat, vault, profile, and all local identities.
- [ ] Define crypto-erase ordering, auto-lock/OS-lock behavior, and optional duress behavior.
- [ ] Inventory copies the app cannot erase (backups, snapshots, pagefile, exported files).
- [ ] Verify wipe behavior on release builds and supported filesystems/devices; inspect dumps/logs.

### Clearnet lane (G11)

- [x] Classify HTTP(S) separately from `jttp`; hand HTTP(S) links to the system browser.
- [ ] Build the isolated in-app clearnet modal with disposable profile on Windows and Linux.
- [ ] Add per-site consent, cookie/download/permission controls, amber lane indicators, and cleanup.
- [ ] Verify JS consent, profile destruction, and file-origin isolation; complete packet-capture review.

### Nickles wallet and plugins (G15, G17)

- [x] Testnet-only concepts and safety constraints are documented.
- [ ] Implement a chain/node, transaction validation, wallet keys, balance/history, faucet, explorer.
- [ ] Build plugin package/catalog/signature/capability-broker and OS/WASM sandbox runtime.
- [ ] Implement install/update/revoke, offline license verification, and first-party Nickles plugin.
- [ ] Complete security, abuse, and legal gates before exposing any wallet or payment behavior.

### Ads and publisher network (G16)

- [x] Local publisher records are encrypted per profile; placement tokens are site/space-bound and
  authenticated with a profile-key HMAC.
- [x] Publishing validates the placement snippet; the browser renders a labelled native ad slot from
  a deterministic local creative catalog with no impression/click beacon.
- [ ] Add creative upload, review/moderation queue, rejection/appeal, and takedown records.
- [ ] Add server-issued, asymmetrically signed slot responses and pinned-key/expiry verification.
- [ ] Add public donation ranking with a documented deterministic input and anti-manipulation rules.
- [ ] Write and publish ad, publisher, moderation, privacy, and takedown policy pages.
- [ ] Verify no impression/click tracking and correct slot signatures with packet captures.

### Shared release verification (G18)

- [ ] Run phase-specific tests and threat-model review after implementation; record evidence and date.
- [ ] Build signed installers/updates, rollback path, SBOM, and platform release artifacts.
- [ ] Complete independent audit, accessibility/i18n review, incident response, and support docs.

### Phase 19 — Handheld, PFS links, live index (3 wk) → **G19**
Tagged live index (`/legenddxI2`), search over PFS, publish-time metadata validation, mesh ephemeral
ECDH, per-opening session identity, click confirmation, top-screen RSS + stylus mini search.
Exit: two devices link with forward secrecy; a tagged site is discoverable and inert until confirmed;
no key or query appears in cleartext on the wire.

## 4. Definition of done per phase

- [ ] Checklist items ticked with evidence.
- [ ] Tests green on Windows x64/arm64 and Linux x64 (arm64 best-effort, Q-08).
- [ ] Threat model updated and reviewed.
- [ ] Docs updated (this file + the relevant spec).
- [ ] 2-minute demo recorded.
- [ ] Gate review held; `docs/gates/G-x.md` signed.

## 5. Release checklist (1.0)

**Build & supply chain**
- [ ] Version stamped; SBOM; hashes published.
- [ ] MSIX + Flatpak + `.deb` signed and verified on clean VMs.
- [ ] Dependencies match `deps.json`; no High/Critical advisories.
- [ ] Minimum WebView2 **and** WebKitGTK versions enforced.

**Security**
- [ ] Audit findings closed; pentest retested.
- [ ] XSS corpus, fuzz corpora, crypto vectors green.
- [ ] Forensic wipe test on release build.
- [ ] Update signature + rollback tests.
- [ ] `security.txt`, advisory template, on-call rota.
- [ ] Kill switch tested end-to-end.

**Privacy**
- [ ] 30-min packet capture shows no unsolicited traffic.
- [ ] Logs contain no plaintext/addresses (scripted grep).
- [ ] Ad slot packet capture shows no tracking.
- [ ] Privacy page and claims reviewed.

**Product**
- [ ] Journeys J1–J10 pass on clean Windows and Linux.
- [ ] First-run ≤ 5 min with 5 testers.
- [ ] Accessibility: screen reader, keyboard-only, 200% zoom, high contrast.
- [ ] Pseudo-locale + RTL smoke.
- [ ] Crash-free 8 h soak; memory within budget.
- [ ] Help content for every error code and claim.

**Operations**
- [ ] Backups restore; monitoring alerts; status page.
- [ ] Rollback plan tested.
- [ ] Support macros for top 20 questions.

## 6. First 10 working days

1. **Day 1** — `Kickl.sln`, projects, CI, `deps.json`, signed debug build.
2. **Day 2** — Avalonia chrome spike: 36 px rail, tabs, Mica on Win 11, blur on KDE/GNOME; screenshot.
3. **Day 3** — `jttp://` spike on WebView2 (standard + secure) and WebKitGTK; static doc + CSP.
4. **Day 4** — full-page capture spike on both engines; measure size/time.
5. **Day 5** — .NET 10 ML-KEM/ML-DSA API spike; one HPKE round-trip.
6. **Day 6** — ADRs 001–011 finalised; engine choice (Q-10) recommended.
7. **Day 7** — `SecureBuffer` + Argon2id + PIN dialog + tests.
8. **Day 8** — AES-GCM blob round-trip + CBOR envelope + fuzz skeleton.
9. **Day 9** — vertical slice: `jttp://pinwheelexplained.com` → KML → blob → PIN → text.
10. **Day 10** — demo, retro, re-estimate, confirm decisions.

## 7. Team and responsibilities

| Role | Owns | Time |
|------|------|------|
| Tech lead / C# | architecture, shell, core, reviews | 1.0 FTE |
| Security engineer | crypto, threat models, CVE program, audit liaison | 0.5 FTE |
| Backend | PHP/MySQL, ops, Sentinel, backups | 0.5 FTE |
| Designer | tokens, rail, motion, dock, home, vault | 0.5 FTE for 10 weeks |
| QA / release | test matrix, VMs, accessibility, release | 0.25 FTE |
| Counsel | currency, ads, privacy claims, terms | as needed |

## 8. Test matrix

| Axis | Values |
|------|--------|
| OS | Windows 10 22H2, 11 23H2/24H2/25H2; Ubuntu 24.04, Fedora 41, Debian 12 |
| Session | Wayland, X11 |
| Arch | x64, arm64 |
| DPI | 100%, 150%, 200%, mixed |
| Theme | Obsidian, Phosphor, Paper, High Contrast |
| Input | mouse, keyboard-only, touch, pen |
| Network | online, offline, 300 ms latency, 5% loss, captive portal |
| State | fresh, upgraded, wiped, corrupt DB, full disk, clock skew |
| Identity | new, restored, second device, revoked |
| Locale | en-US, de-DE, ar-SA (RTL), ja-JP |

## 9. Cost sketch (USD)

| Item | Cost |
|------|------|
| EV certificate | 300–600/yr |
| VPS + storage + CDN | 40–200/mo at beta scale |
| Crypto audit | 25,000–45,000 |
| Pentest | 8,000–15,000 |
| Bug bounty (year 1) | 5,000–15,000 |
| Legal (currency, ads, privacy) | 8,000–25,000 |
| **Pre-launch (excl. labour)** | **≈ 50,000–100,000** |

## 10. What would make this fail

1. Building the home/arcade before the sealed surface and the vertical slice.
2. Inventing a protocol to satisfy a phrase in the brief.
3. Treating `jttp://` as "just a skin" over `https://`.
4. Shipping mail/chat before panic/wipe.
5. Letting the registry become a per-name query API "for performance".
6. Trusting plugin or ad code with ambient authority.
7. Publishing AI-drafted advisories without a human.
8. Skipping the audit because the budget ran out.
