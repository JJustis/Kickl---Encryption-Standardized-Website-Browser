# 01 — Product, Scope, and Success Criteria

## 1. Personas

| Persona | Context | Primary needs | What must never happen |
|---------|---------|---------------|------------------------|
| **Nyx** — security researcher | Windows 11 laptop, VPN, threat-aware | sealed research notes from peers, verifiable identities, no telemetry, fast panic | a page silently calls home; a cached note is readable without PIN |
| **Ari** — journalist-adjacent organiser | Medium threat model, non-expert | mail + chat in one place, easy contact exchange, clear "what is visible" labels | accidental plaintext send; ambiguous padlock states |
| **Dev** — tinkerer | Wants the cyberdeck aesthetic | themes, keyboard-first, tabs in the bar, Nickles toy | blurry text, dropped animations, unusable at 125% DPI |
| **Rae** — recipient, no Kickl yet | Receives an exported contact bundle | one-click import, works offline, understands trust prompt | importing a bundle silently grants access to everything |

## 2. Core journeys (acceptance-level)

**J1 — First run (≤ 5 min)**
1. Install (signed MSIX), launch, choose theme. 2. Create identity → 24-word recovery phrase with a
"write this down" gate (cannot continue until 3 random words are re-entered). 3. Set PIN (min 6
digits; strength meter; show an unlock-time estimate only after measuring on supported hardware). 4. Choose sync mode:
`Manual` / `Pinwheel (60–300 s)` / `Offline`. 5. Land on `jttp://home` with an address shown and a
copy button.

**J2 — Receive a sealed page**
1. Paste `jttp://nyx.custom/research/2026-10` in the omnibox. 2. Page skeleton renders with sealed
blob placeholders (shimmer, lock glyph, byte size). 3. Click a blob → PIN prompt (cached for
5 min) → fragment appears; unopened blobs stay sealed. 4. "Save to vault" → PNG container written,
thumbnail in vault grid. 5. Later, open vault item → PIN → previously opened blobs visible,
unopened still sealed. 6. "Refresh from owner" fetches a signed newer snapshot; changed blobs are
marked.

**J3 — Mail a friend**
1. Add contact by `kb1…` or 8-digit short code → out-of-band verify (safety number words).
2. Compose with optional per-message key ("Extra privacy" toggle). 3. Send → queued envelope,
sealed to recipient's KEM key, sender sealed. 4. Server stores opaque blob; recipient polls and
decrypts. 5. Attachment > 5 MB goes to blob store, only the key travels in the mail.

**J4 — Live chat**
1. Open dock (chat lobe), pick contact/group. 2. Handshake (X3DH + hybrid PQ) → MLS group for ≥ 3.
3. Messages appear with delivery ticks; epoch rotates every 60 s of activity or 100 messages.
4. Panic → all sessions torn down, keys zeroised, store wiped per chosen scope.

**J5 — Clearnet**
1. Type `example.com` → omnibox flags "open web". 2. Modal mini-browser opens with a banner:
"JavaScript off · cookies blocked · separate profile". 3. User may enable JS for this site only,
with a confirm. 4. "Expand" → full window. 5. Closing the modal destroys the profile.

**J6 — Panic**
1. Toolbar panic control: click = lock (requires PIN to resume); hold 3 s = arm wipe menu:
`Session` / `Chat + Mail` / `Vault` / `Everything (crypto-erase)`. 2. Type `WIPE` to confirm for
the last two. 3. Progress shown; keys destroyed first, files second. 4. App restarts to first-run.

## 3. Scope

### In scope for 1.0
- Windows 10 22H2+ / Windows 11, x64 and arm64, per-monitor DPI.
- Sealed surface: `jttp://` standard scheme, KML markup, OSS styles, blob unlock, CSP.
- Registry: public signed snapshots, domain registration UI, owner key management, transparency log.
- Identity: `kb1…` addresses, short codes, device certification, recovery phrase.
- Mail: compose/read/search, contacts, bundles, sealed sender, attachments, queue/retry.
- Chat: 1:1 Double Ratchet, groups via MLS, presence, typing indicator (opt-out), file drop.
- Vault: PNG containers, thumbnails, per-blob state, refresh-from-owner.
- Panic: lock, scoped wipe, crypto-erase, auto-lock timer, idle-on-screen-lock.
- Clearnet modal: JS off default, cookie blocking, per-site allow, disposable profile.
- Server reference implementation (PHP/MySQL) + deployment runbook.
- Update system: signed manifests, staged rollout, rollback protection.

### Out of scope for 1.0
- Mobile/tablet, macOS, Linux (architecture keeps core portable; no UI work).
- Real-money Nickles, exchange, ICO, fiat on/off ramps.
- Extensions/plugin API (a plugin surface is a 2.0 conversation with its own threat model).
- Sync of messages across devices beyond signed device enrollment (2.0).
- Anonymity network built in-house (Tor/OHTTP integration is opt-in transport, 1.1).
- Rendering arbitrary clearnet HTML on the sealed surface.
- Password manager, ad blocking for clearnet, media codecs beyond WebView2 defaults.

### Explicit non-goals (and why)
- **"Untraceable."** Polling, relays, and IP exposure make absolute anonymity false. We promise
  minimisation and give the user Tor as a supported transport later.
- **"Unbreakable."** Crypto breaks; implementations fail. We promise standards + audit + fast patch.
- **"Zero knowledge server" as marketing.** The server sees metadata. We publish exactly what.
- **Undeniable deniability.** Coercion resistance is a research problem; we do not claim it.

## 4. UX principles

1. **Viewport first.** Chrome rail ≤ 40 px at 100% DPI. No second toolbar. No bookmarks bar by
   default (a "shelf" overlay on `Ctrl+Shift+B`).
2. **Every lock is honest.** A sealed blob shows *sealed*, *unlocked this session*, or *unlocked and
   cached* — never a vague padlock.
3. **Destructive actions are physical.** Hold, type, or two-step. Never a single red button.
4. **Quiet by default, loud on request.** Animations respect reduced-motion; "hacker" effects are a
   theme, not a baseline.
5. **Keyboard complete.** Every action has a shortcut; the UI is usable without a mouse.
6. **No dark patterns.** No pre-checked sharing, no "recommended" toggles, no telemetry consent
   buried in install.

## 5. Success metrics (measured, not vanity)

| Metric | Target | Method |
|--------|--------|--------|
| Cold start to interactive | < 1.5 s (p95, 4-core/8 GB) | ETW trace in CI perf job |
| Tab switch | < 120 ms p95 | instrumentation |
| Sealed page first paint (skeleton) | < 400 ms p95 | instrumentation |
| Blob unlock | < 250 ms after PIN cached | instrumentation |
| Memory, 10 sealed tabs | < 900 MB | soak test |
| Crash-free sessions | > 99.5% | local opt-in crash counter only |
| Sealed-at-rest violations | 0 | forensic test in CI (strings scan of data dir) |
| XSS bypasses in corpus | 0 | 500-vector suite + fuzz |
| Critical CVE patch time | ≤ 72 h from upstream disclosure | incident log |
| Audit findings open at release | 0 Critical/High | audit report |

## 6. Accessibility & i18n

- WCAG 2.2 AA: contrast, focus rings (2 px, offset 2), 200% zoom, no colour-only status.
- Screen reader: AutomationProperties on rail, tabs, dock, blob list; live regions for unread counts.
- Reduced motion: replaces glitch/scanline with 120 ms fades.
- High contrast theme maps tokens to system colours.
- All strings in `.resx`; pseudo-localisation in CI; RTL layout mirroring tested (Arabic stub).
- Fonts: UI = Inter/Segoe UI Variable; mono = Cascadia Code; both bundled with licences recorded.

## 7. Naming and copy rules

- Never: "unbreakable", "military-grade", "anonymous", "untraceable", "no one can see".
- Prefer: "sealed on this device", "the server relays ciphertext", "metadata is minimised, not
  eliminated".
- Every crypto feature links to a plain-language explainer page (`jttp://help/crypto`).

---

## 8. Decisions log (fill this in at G0)

| ID | Question | Options | Decision | Date | Owner |
|----|----------|---------|----------|------|-------|
| D1 | Platform | Windows only / +Linux | | | |
| D2 | Scheme | `jttp://` / intercept `.custom` | | | |
| D3 | Registry | own / interop secupgrade | | | |
| D4 | Team & date | | | | |
| D5 | Nickles | testnet / real | | | |
| D6 | Server operator & SLA | | | | |
| D7 | Names/trademark | | | | |
| D8 | Audit budget | | | | |
| D9 | Recovery: phrase only, or optional encrypted escrow? | | | | |
| D10 | Default poll interval & Tor availability at 1.0 | | | | |

## 9. Glossary

**Sealed surface** — the `jttp://` renderer. **Clearnet** — ordinary web via the modal.
**Blob** — an individually encrypted content block. **Vault** — local store of PNG containers.
**Pinwheel** — the polling/relay network. **Short code** — 8-digit directory alias.
**KEK/DEK** — key-encryption key / data-encryption key. **Crypto-erase** — destroy keys so
ciphertext is unrecoverable (the only defensible "secure delete" on SSDs).
**Snapshot** — signed, Merkle-rooted registry state. **Epoch** — a key-rotation period.
