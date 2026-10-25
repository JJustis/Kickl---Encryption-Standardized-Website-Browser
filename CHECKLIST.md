# Kickl Browser — Master Checklist

Tick a box only when the evidence exists (test, screenshot, signed artifact, or signed gate note in
`docs/gates/`). **Bold** items are gate blockers.

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked

---

## 0. First goal — `jttp://pinwheelexplained.com` (vertical slice)

The acceptance test for the first milestone: type the URL, see a real sealed page built from KML,
with at least one encrypted blob that unlocks with a PIN, and a panic lock.

- [x] Solution builds on Windows with `dotnet build`
- [x] `Kickl.Core` library + `Kickl.Core.Tests` exist
- [x] AES-256-GCM NIST known-answer vector; HKDF-SHA-512 fixed-output vector + round-trip
- [x] PIN → versioned Argon2id (m=64 MiB, t=3, p=4) → KEK → unwrap master; RFC 9106 vector passes; legacy PBKDF2/p=1 profiles migrate after successful unlock
- [x] KML parser accepts a minimal document and rejects unknown elements
- [x] Local registry fixture resolves `pinwheelexplained.com`
- [x] Document loader serves a signed fixture document over `jttp://`
- [x] Sealed blob stays ciphertext until unlocked
- [x] Unlock with PIN renders the blob text
- [x] "Lock" clears the in-memory session key and re-seals the view
- [x] `jttp://pinwheelexplained.com` opens end-to-end in the app (or a headless host)
- [x] Zero plaintext of the blob exists in the data directory (grep test)
- [~] Demo recorded + checklist updated

### Browser-first slice (current)

- [x] Screen 1 browser + screen 2 publications and saved items (`artifacts/browser-screen.png`)
- [x] Screen-two feed reads local `C:\xampp\htdocs\rssfeed.xml` on startup and every five minutes; entries stay inert until opened
- [x] No-address home is a split surface: games array first, PFS tag search second (`artifacts/home.png`)
- [x] Native tic-tac-toe arcade demo; queued wasm-1 game cards
- [x] Home search uses the PFS `Search` path; results stay inert until the confirmation sheet
- [x] Unknown `jttp://` name opens an inline creation template instead of a dead end
- [x] New custom name is pre-filled with a complete example page and live preview (`artifacts/example-site.png`)
- [x] `jttp://name.rss` opens the next-story XML template in browser space (`artifacts/rss-xml-editor.png`)
- [x] RSS story template fields: image, headline, author, date, location, story, tags (no inline HTML/SVG)
- [x] RSS XML `<item>` parses into the fixed story fields; nested inline SVG is text, not a visual
- [x] Free `.com`/custom pages: inline HTML/SVG, optional text, optional image, headline and tags
- [x] Direct origin-device fetch over RSA-OAEP-wrapped AES-256-GCM + RSA-PSS response signature
- [x] Inline HTML/SVG visual block with sanitizer and native renderer (`artifacts/svg-inline.png`)
- [x] Data-URL images inside inline SVG are extracted, sealed as AES-256-GCM blobs, and rewritten to `kickl-blob:<id>`
- [x] Click-to-unlock sealed inline image renders in place; bytes are zeroed on lock/navigation/close (`artifacts/sealed-inline.png`)
- [x] NCKL publisher console at `jttp://secupgrade.com/adnetworking/publisher`: site-bound token, `nickles.js` manifest, copyable snippet (`artifacts/nckl-publisher.png`)
- [x] Publisher snippet is validated at publish; a copied token fails closed; only one native `<ad>` block per page (`artifacts/nckl-ad-site.png`)
- [x] NCKL creative rotates deterministically per 10-minute bucket; `AD` label always visible; destination opens through the clearnet confirmation sheet
- [x] Home NCKL slot is a full-width footer bar above the status bar (no overlay), rotates from the local catalog every 10 minutes, and remembers its dismiss-to-strip choice locally
- [x] Tests cover inline SVG sanitization, starter example publishing, sealed-image extraction/publish/unlock, RSS XML parsing/template rejection and SecUpgrade feed metadata, story publishing, direct fetch, tamper failure, NCKL publisher binding, and Argon2id RFC vector/profile migrations (105 tests)

---

## Phase 0 — Decisions & discovery  → **G0**

- [ ] **D1 Windows + Linux confirmed** (`docs/00-decisions.md` ADR-001)
- [ ] **D2 `jttp://` confirmed** (ADR-002)
- [ ] **D3 own registry at secupgrade.com confirmed** (ADR-003)
- [ ] **D4 name "Kickl Browser" + spelling confirmed** (ADR-004)
- [ ] **D5 Nickles as plugin + "qt" meaning confirmed** (ADR-005, Q-03)
- [ ] **D6 instant claim flow confirmed** (ADR-006)
- [ ] **D7 home split confirmed** (ADR-007)
- [ ] **D8 ad slot position/size confirmed** (ADR-008)
- [ ] **D9 plugin payments confirmed** (ADR-009)
- [ ] **D10 Sentinel confirmed** (ADR-010)
- [ ] **D11 input privacy confirmed** (ADR-011: no mouse tracking, per-keystroke AES)
- [ ] **D12 per-identity data + user-key gate confirmed** (ADR-011)
- [ ] Threat model v1 written (`docs/threat/`)
- [ ] Standards register reviewed
- [ ] Trademark screen for "Kickl" / "Nickles"
- [ ] Audit budget approved (Q-01)
- [ ] Linux packaging decided (Q-02)
- [ ] WebKitGTK vs CEF spike decided (Q-10)
- [ ] Legal entity + terms/privacy owner named (Q-09)
- [ ] Gate note `docs/gates/G0.md` signed

## Phase 1 — Engineering baseline → **G1**

- [ ] `Kickl.sln`, `Directory.Build.props`, `.editorconfig`
- [ ] Nullable + warnings-as-errors + analyzers
- [ ] Project layout per `docs/02` §2 (all projects compile empty)
- [ ] CI: build, test, format, analyze (Windows + Linux runners)
- [ ] CI: SBOM (CycloneDX), dependency review, secret scan, licence check
- [ ] `deps.json` policy + pinned versions + lock files
- [ ] Branch protection + PR template with threat-model checkbox
- [ ] Safe logger with allowlist serialisation
- [ ] Crash-dump policy (off by default)
- [ ] Code-signing certificate + dev signing script
- [ ] MSIX skeleton installs on clean Windows 10/11 VM
- [ ] Flatpak manifest + `.deb` skeleton build on Ubuntu 24.04
- [ ] Gate note `G1.md` signed

## Phase 2 — Crypto core → **G2**

- [ ] `SecureBuffer` (pinned, zeroised) + tests
- [ ] AES-256-GCM wrapper, nonce discipline, rekey thresholds
- [ ] Guard test: no symmetric AEAD other than AES-256-GCM in the dependency graph
- [ ] AES-256-GCM-SIV for multi-recipient blobs
- [ ] HKDF-SHA-512 with labelled `info` per use
- [x] Argon2id PIN KDF with versioned params; legacy PBKDF2 profile migration on successful unlock
- [ ] X25519 + ML-KEM-768 hybrid KEM (or documented fallback)
- [ ] Ed25519 + ML-DSA-65 hybrid signatures
- [ ] HPKE seal/open wrapper + RFC 9180 vectors
- [ ] Noise_XX state machine + rekey + replay window
- [ ] Double Ratchet + X3DH (used in Phase 9)
- [ ] Keystore: Windows DPAPI-NG/TPM path
- [ ] Keystore: Linux Secret Service + TPM2 path
- [ ] Keystore fallback (passphrase-only) with warning
- [ ] Recovery phrase generate/restore + 3-word confirmation gate
- [ ] Device certificate issue/verify/revoke
- [ ] ACVP + Wycheproof + RFC vectors green
- [ ] `dudect`-style timing check green
- [ ] Fuzz: container, CBOR, HPKE 24 h clean
- [ ] Session secret: HKDF from vault master + per-unlock nonce
- [ ] `KeystrokeShield`: per-char AES-256-GCM cells, AAD binds session/field/counter
- [ ] `Reveal()` into pinned zeroised buffer; `Lock()` kills it
- [ ] Cross-field / reorder / replay cell tests fail closed
- [ ] Byte-scan test: field buffer contains no typed substring
- [ ] External crypto design review, no Critical/High open
- [ ] Gate note `G2.md` signed

## Phase 3 — Shell (Avalonia, Win + Linux) → **G3**

- [x] Local entry gate: sign in or create encrypted local profile with a PIN; recovery/FIDO2/TPM setup remains open
- [ ] Per-identity profile roots + switching (full key/session swap)
- [ ] Auto-lock chip + "stay unlocked" (max 12 h, revoked on OS lock)
- [ ] Pointer events used for paint only; no pointer logging surface
- [ ] Avalonia app boots on Windows and Linux
- [ ] Custom window chrome (no OS title bar) on both
- [ ] 36 px rail at 100% DPI, 34–44 clamp, DPI-scaled
- [ ] Tabs inside the rail: active/inactive/loading/pinned/audio/discarded
- [ ] Tab drag-reorder; drag-out to new window
- [ ] Omnibox: parse, normalise, scheme rewrite, security chip
- [ ] Local suggestions (no network) with keyboard navigation
- [ ] Window snap: Windows Snap Layouts hit-test; Linux compositor-native
- [ ] Theme tokens + Obsidian/Phosphor/Paper + high contrast
- [ ] `LightButton` states + reduced-motion variant
- [ ] Dock shell (collapsed pill + two lobes)
- [ ] Settings (6 sections) + search
- [ ] Keyboard map complete
- [ ] Accessibility: focus, contrast, screen reader smoke test
- [ ] 200% zoom + mixed-DPI test
- [ ] 60 fps scroll/animation trace
- [ ] Gate note `G3.md` signed

## Phase 4 — Sealed surface (`jttp://`, KML, KSS) → **G4**

- [ ] Cross-platform web engine abstraction (`IWebSurface`)
- [ ] Windows: WebView2 custom scheme registered secure + standard
- [ ] Linux: WebKitGTK URI scheme registered secure
- [x] `jttp://name.tld` canonical parser + `.custom` + real-TLD rules
- [ ] Typing `http(s)://x.custom` rewrites to `jttp://x.custom`
- [x] KML parser (strict, reject unknown) + limits
- [ ] KSS compiler (allowlist, no external `url()`, caps)
- [ ] Renderer: skeleton + sealed placeholders
- [x] Sanitizer for inline HTML/SVG (allowlist; no script, forms, external refs) + tests
- [x] Native inline SVG renderer (`Avalonia.Svg.Skia`) behind the sanitized visual block
- [x] Sealed image references inside inline SVG (`kickl-blob:`) with click-to-unlock + tests
- [ ] CSP injection (`default-src 'none'`)
- [ ] JS engine disabled on sealed surface (both engines)
- [ ] Network kill switch: zero outbound requests from sealed docs
- [ ] 500-payload XSS corpus: 0 bypasses
- [ ] Parser/SSE fuzz 24 h clean
- [ ] Blob rendering + per-blob state UI
- [ ] Gate note `G4.md` signed

## Phase 5 — Registry, identity, cookies → **G5**

- [x] `kb1…` address encode/decode + Bech32m vectors (`tests/Kickl.Core.Tests/IdentityTests.cs`)
- [x] Short code generate/validate (MOD 97-10)
- [x] Safety numbers
- [~] Snapshot client + ETag + delta apply (signed snapshot/delta client with key pinning, merge, PHP/MariaDB server and CLI sync done; ETag, head-signature verification, consistency proofs, and in-app wiring pending)
- [x] Merkle inclusion + consistency proof verifier (`MerkleLog`, RFC 6962 vectors and pair tests)
- [x] Rollback + equivocation detection (`RegistryMirror`, tests)
- [~] TOFU pin + key-change interstitial + 7-day cool-down (pins, detection, shield approval panel, and snapshot sync done; automatic interstitial and 7-day cool-down pending)
- [ ] Local resolution cache ("DDNS") + inspector
- [ ] Zero-packet test: 100 random name navigations
- [ ] Registration client (PoW, sign, retry)
- [ ] **Instant claim sheet for unknown names** (ADR-006)
- [ ] Did-you-mean / typo protection
- [x] Starter page builder (inline browser template) + publish
- [ ] New-site badge (< 7 days) + abuse report action
- [ ] Domain certificate viewer (record, log index, pin)
- [ ] Cookie jar: encrypted, partitioned, caps, SameSite
- [ ] Cookie inspector UI + clear-on-close
- [ ] Gate note `G5.md` signed

## Phase 6 — Blobs → **G6**

- [ ] KML-DOC envelope (CBOR) + hybrid signature verify
- [ ] Per-blob HPKE key wrap
- [ ] Multi-recipient wrap
- [ ] PIN gate + 5-min capability cache
- [ ] Session cache (`SecureBuffer`, 32 MiB cap)
- [ ] Per-blob `sealed/opened/failed` state
- [ ] Audit line per unlock (no content)
- [ ] Wrong-PIN / tamper / wrong-key fail closed
- [ ] Blob type allowlist + size caps + streaming open
- [ ] Sealed-at-rest grep test in CI
- [ ] Gate note `G6.md` signed

## Phase 7 — Vault → **G7**

- [ ] Full-page capture on WebView2 (CDP)
- [ ] Full-page capture on WebKitGTK (snapshot + stitch)
- [ ] Redaction of unopened blobs before capture
- [ ] `.kickl.png` container (`kiKl` chunk) + atomic write
- [ ] Thumbnail (blur/solid/sharp) + honesty copy
- [ ] Optional LSB manifest + fallback
- [ ] Blob ratchet + re-wrap schedule
- [ ] Vault gallery (virtualised) + viewer overlays
- [ ] "Refresh from owner" + diff states
- [ ] Export (encrypted) + import
- [ ] Torn-write recovery + container fuzz
- [ ] Forensic wipe test
- [ ] Gate note `G7.md` signed

## Phase 8 — Mail → **G8**

- [x] Direct KicklMail v1: Pinwheel credential lookup, device-to-device delivery, no server mailbox
- [x] ML-DSA-65 identity/envelope signatures, RSA-3072-wrapped per-message AES-256, Brotli 90k body,
  PNG private-chunk carrier
- [x] Password-sealed SQLite vault, click-to-open inbox rows, image-page reader
- [ ] Mail outbox, retry, receipts, Bcc, sealed sender, attachments
- [ ] SQLCipher store + migrations
- [ ] Mailbox hash + epoch + token flow
- [ ] Seal/open envelope + sealed sender
- [ ] Bcc as separate envelopes
- [ ] Outbox, idempotency, retry, receipts
- [ ] Attachments → blob route
- [ ] Contact bundles export/import + signature checks
- [ ] Vouches (signed, revocable, consent-based)
- [ ] Spam: allowlist, PoW, Requests folder
- [ ] Local search (blind index)
- [ ] Compose/read UI in dock
- [ ] Packet capture + DB dump: no plaintext
- [ ] Gate note `G8.md` signed

## Phase 9 — FreeChat → **G9**

- [x] Pinwheel FreeChat v1: native room client (ML-KEM-1024 + AES-256-GCM), PNG messages,
  encrypted local credentials, shareable ID, per-ID blocklist
- [ ] X3DH + Double Ratchet session
- [ ] MLS group wrapper + Welcome/Commit
- [ ] 1:1 direct P2P + relay fallback
- [ ] Presence (opt-in, bucketed) + typing (opt-in)
- [ ] Disappearing messages + burn notice
- [ ] Attachments + previews
- [ ] Thread list (no preview by default) + composer
- [ ] Notifications without message text
- [ ] FS/PCS test: epoch/ratchet advance, old keys zeroised
- [ ] Out-of-order/loss/replay tests
- [ ] 20-member group churn + 8 h soak
- [ ] Gate note `G9.md` signed

## Phase 10 — Panic / lock / wipe → **G10**

- [ ] Click = lock; keys evicted; PIN to resume
- [ ] Hold 3 s = arm menu
- [ ] Scopes: session / chat+mail / vault / everything
- [ ] `WIPE` confirmation + hold for total
- [ ] Keys-first destruction order
- [ ] WebView2/WebKit profile deletion
- [ ] Log deletion + `/tmp` cleanup
- [ ] Auto-lock on idle + OS screen lock
- [ ] Wipe progress log (terminal style)
- [ ] Known-copies report (VSS, backups, pagefile)
- [ ] Duress PIN (optional, off, warned)
- [ ] VM image + physical SSD erase verification
- [ ] Gate note `G10.md` signed

## Phase 11 — Clearnet modal → **G11**

- [ ] Separate disposable profile (Win + Linux)
- [ ] JS off by default; per-site enable with confirm
- [ ] Cookie blocking + no history + no autofill
- [ ] Downloads quarantine; no auto-open; `file://` blocked
- [ ] Permissions default-deny (camera/mic/geo/notifications)
- [ ] Amber signalling in tab/chip/border
- [ ] Expand/close + profile destruction
- [ ] Handoff clearnet → `jttp` with transition
- [ ] Forensic check: profile gone after close
- [ ] Gate note `G11.md` signed

## Phase 12 — Server, ops, registry hosting → **G12**

- [x] PHP registry skeleton + router allowlist + strict types (PHP 8.2; XAMPP)
- [~] MySQL migrations (registry tables done; mail, relay, blobs, nonces pending)
- [~] Merkle log + signed checkpoints (root signed inside snapshots/deltas; standalone checkpoints pending)
- [~] Registry register/renew/delta/snapshot (register, snapshot, delta, head, key done; renew, owner signatures, PoW pending)
- [x] Offline hybrid signer (`tools/Kickl.RegistrySigner`: keygen, snapshot, delta)
- [x] Client `registry sync` + `registry status` against the server (local end-to-end tested)
- [x] Apache subpath support (`/apicalls` detection, rewrite, deploy bootstrap)
- [x] TLS deployment at `https://secupgrade.com/apicalls` (XAMPP Apache; health, register, delta sync verified)
- [ ] Mail deposit/fetch/ack/token
- [ ] Rendezvous + relay + GC
- [ ] Blob store (content-addressed, outside docroot)
- [ ] Rate limits + PoW verification
- [ ] nginx TLS 1.3 + HSTS + body limits + no directory listing
- [ ] Backups (encrypted) + restore drill < 1 h
- [ ] Monitoring (aggregate only) + status page
- [ ] `security.txt` + disclosure page
- [ ] DAST clean + load test 10×
- [ ] Kill switch (signed config)
- [ ] Runbook: deploy, rotate, incident
- [ ] Gate note `G12.md` signed

## Phase 13 — Sentinel CVE watcher → **G13**

- [ ] Feed workers (NVD, OSV, MSRC, GHSA, PHP, MySQL, nginx, Chromium, WebKitGTK)
- [ ] Normalise + dedupe + raw storage
- [ ] `inventory.json` from CI SBOM
- [ ] Match engine (exact/range/component)
- [ ] LLM drafter with strict JSON schema + injection guard
- [ ] Human approval queue UI
- [ ] Signed `/v1/advisories` feed
- [ ] Client shield badge + remediation page
- [ ] Kill switch one-click
- [ ] Alerting (S0/S1) + daily digest
- [ ] False-positive tracking
- [ ] Gate note `G13.md` signed

## Phase 14 — Home: LegendDX + Arcade → **G14**

- [ ] Home split 50/50, draggable, stacks < 900 px
- [ ] LegendDX local search over registry
- [ ] Vault/contacts search
- [ ] Explicit "search the open web" consent + clearnet handoff
- [ ] Recent/favourites/first-run states
- [ ] Arcade feed + this week/all time/new
- [ ] Game bundle format + manifest + signature
- [ ] Sandboxed game runtime (WASM + JS shim, no network)
- [ ] Arcade API (`score`, `storage`, `profile`, `exit`)
- [ ] Memory/CPU watchdog + crash containment
- [ ] Publish flow + validation + scan
- [ ] Leaderboards + likes + streak + remix lineage
- [ ] Profile page
- [ ] Starter pack (3 CC0 games)
- [ ] Moderation + report + takedown log
- [ ] Gate note `G14.md` signed

## Phase 15 — Plugins, market, Nickles plugin → **G15**

- [ ] Manifest schema + validation
- [ ] Package format (zip rules, path safety, caps)
- [ ] Author signature + catalog countersignature
- [ ] Capability broker + per-call consent
- [ ] Sandbox: Windows AppContainer + job object
- [ ] Sandbox: Linux bubblewrap + seccomp
- [ ] WASM host with allowlisted imports
- [ ] KML panel renderer (bounded)
- [ ] Install/update/uninstall + capability-change consent
- [ ] Revocation list + disable flow
- [ ] Catalog UI + capability sheet copy
- [ ] Reviews (license/age gated) + ranking
- [ ] Payment invoice (BTC unique address) + confirmation
- [ ] NKL testnet invoice path
- [ ] License token (signed, offline-verifiable)
- [ ] **Nickles plugin**: wallet + **quote panel ("qt")** + faucet + ad client
- [ ] Sandbox escape test suite
- [ ] Gate note `G15.md` signed

## Phase 16 — Ad network (Nickles only) → **G16**

- [~] Placement registry (`home.footer` full-width bar implemented; server-side registry/ranking pending)
- [x] Opt-in publisher placement (`site.inline`) with site-bound token, `nickles.js` manifest, and native `<ad>` block (slice)
- [x] Home slot UI + `AD` label + dismiss-to-strip
- [ ] No tracking / no cookies / no redirector (verified by packet capture)
- [ ] Public donation ranking + signed table page
- [ ] Deterministic 10-min bucket rotation (rendezvous hash) — local signed catalog in the slice
- [ ] Creative upload + scan + human moderation
- [ ] BTC/NKL invoices + on-chain verification
- [ ] Signed slot response + creative hash pin
- [ ] Takedown log + appeal policy
- [ ] Policy page (`jttp://ads`) + rejected categories
- [ ] Gate note `G16.md` signed

## Phase 17 — Nickles testnet → **G17**

- [ ] Chain params + genesis + address HRP `nkl1`
- [ ] Tx format + hybrid signatures + replay domain
- [ ] Block validation + PoW + retarget + checkpoint sync
- [ ] Wallet (balance/send/receive/history)
- [ ] Faucet + abuse limits
- [ ] Explorer (read-only)
- [ ] Permanent `TESTNET — NO VALUE` banner
- [ ] Release compile-time gate + signed remote gate
- [ ] Legal sign-off (money transmission/securities/sanctions/tax)
- [ ] Gate note `G17.md` signed

## Phase 18 — Hardening, audit, beta, release → **G18**

- [ ] External crypto audit + remediation
- [ ] External pentest (client + API) + retest
- [ ] Bug bounty live
- [ ] Red-team tabletop + malware simulation
- [ ] Performance soak (8 h, 10 tabs, 900 MB budget)
- [ ] Forensic wipe re-test on release build
- [ ] Update signature + rollback + staged rollout test
- [ ] Kill switch end-to-end
- [ ] Accessibility audit (WCAG 2.2 AA)
- [ ] Localisation pseudo-locale + RTL smoke
- [ ] Help pages for every error code + security claim
- [ ] Privacy page + in-app claims reviewed
- [ ] Terms + acceptable use + ad/plugin policies published
- [ ] Support runbook + top-20 macros
- [ ] Rollback plan tested
- [ ] **Zero open Critical/High findings**
- [ ] Release artifacts signed, hashed, SBOM published
- [ ] Gate note `G18.md` signed

---

## Phase 19 — Handheld, PFS links, live index → **G19**

- [x] PFS name lookup (ephemeral P-256 ECDH, HKDF-SHA384, AES-256-GCM)
- [x] PFS live-index search (query and results inside the sealed frame)
- [x] Required publish metadata: title, headline, 1–8 tags, server-set `updated`
- [x] Search ranks tag matches first; closed names excluded; 50-result cap
- [x] Publish sends package SHA-256; receiver verifies before parse/render
- [x] Wrong SHA fails closed and increments the registry attack counter
- [x] Mesh link uses ephemeral ECDH per link; RSA-3072 signs only
- [x] Session P-256 identity regenerated per browser opening and destroyed on lock
- [x] `search` CLI prints the inert-result / device-confirmation rule
- [ ] `secupgrade.com/legenddxI2` deployment + signed snapshots
- [ ] Live re-fetch of site metadata (current `updated` is publish-time)
- [ ] Click confirmation sheet in the Avalonia shell
- [ ] Top-screen RSS scroller + stylus mini search
- [ ] Site IDE + instant claim (`claim`) wired to publish
- [ ] Four-port USB / security-key hardware validation
- [ ] Gate note `G19.md` signed

---

## Cross-cutting (always)

- [ ] No plaintext, keys, addresses, or message bodies in logs
- [ ] Every error has a stable code + help link
- [ ] Every security claim has a threat-model row + test
- [ ] Every new dependency passes `deps.json` policy
- [ ] Every UI string passes the "no over-claim" review
- [ ] Every phase updates this checklist and `docs/gates/`
