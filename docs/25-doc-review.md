# 25 — Documentation review (2026-10-27)

Every document was checked against the code as it stands after the security work in `docs/24`. **Method, stated honestly:** I searched all
documents for the topics that changed (mail transport and endpoints, FreeChat, `.kcert` and owner binding, the CA, mesh links, PNG/SVG
limits, rate limits, key storage, test counts, "plaintext" and "bearer" claims) and read every hit in context; documents with no hit were
checked by their headings and opening scope against the code layout rather than line by line. The design documents (01–16, 19) are
specifications written before the implementation; where they describe something that is now built differently, the built behaviour is in the
"as shipped" sections added below or in `docs/08`, `17`, `20`, `21`, `23`, `24`.

Status key: **updated** = text changed in this pass · **ok** = read, nothing contradicts the code · **spec** = a design document; not a
description of shipped behaviour.

| Document | Status | What was checked / changed |
|---|---|---|
| `README.md` | updated | Added owner binding, pinned-TLS mail, FreeChat pinning and signing; document index now lists 22–25 |
| `INSTALL.md` | updated | Test count (about 390), bearer-credential note now says what owner binding changes, mail port and firewall note, FreeChat fingerprints |
| `server/README.md` | updated | Mail endpoint format (https + pin), new rate-limit section, "no rate limits" gap removed |
| `reference/api.md` | updated | Mail section marked as the planned mailbox API; the shipped direct mail is described and pointed to `docs/08` |
| `CHECKLIST.md` | updated | Ticked the TLS mail, FreeChat and audit items; open items (root key offline, independent pentest, signed room keys) added unticked |
| `docs/00-decisions.md` | updated | Added decisions D-S1…D-S5 (pinned TLS mail, one CA, owner binding, compatible FreeChat, DPAPI keys). The Linux "No DPAPI" line is about Linux and is still right |
| `docs/01-product-scope.md` | spec / ok | No claim about mail transport, ownership or FreeChat internals |
| `docs/02-architecture.md` | spec / ok | Keystore description is the design (DPAPI-NG/TPM); the profile key is wrapped by the PIN as `docs/03` says |
| `docs/03-cryptography.md` | spec / ok | Primitives and parameters unchanged; the "no `http://`" rule at line ~256 is about page fetches |
| `docs/04-pinwheel-network.md` | spec | Store-and-forward mailbox design; shipped direct mail is in `docs/08` §12 |
| `docs/05-registry-identity.md` | spec / ok | |
| `docs/06-shell-ux.md` | spec / ok | |
| `docs/07-blob-markup-cache.md` | ok | The 64 MP page-image tiling cap is a different limit from the 16 MP stego reader cap (`docs/18`) |
| `docs/08-mail-freechat.md` | updated | §12 gains the pinned-TLS transport; new §12.1 FreeChat as shipped (https only, TOFU key pins, signed messages, bounds); checklist ticked |
| `docs/09-nickles.md` | spec / ok | |
| `docs/10-security-program.md` | updated | Status note pointing to the audit |
| `docs/11-roadmap-gates.md` | spec | Roadmap; not re-planned in this pass |
| `docs/12-sentinel-cve-watcher.md` | spec / ok | |
| `docs/13-home-legenddx-arcade.md` | ok | |
| `docs/14-plugins-market.md` | spec / ok | |
| `docs/15-ad-network.md` | spec / ok | Its rate-limit endpoints are the ad network's, not the registry's |
| `docs/16-input-privacy-and-sessions.md` | ok | |
| `docs/17-ownership-titles.md` | updated | New §9.1: the shipped shop + CA claim path, owner binding, sealed certificate copies; older registry-token rows labelled as the earlier slice |
| `docs/18-sealed-pages-and-stego-cache.md` | updated | New "Reader limits" (canvas cap, inflation cap, text-chunk cap) |
| `docs/19-hardware-device.md` | spec | Mesh is described as hardware design; the library-level mesh hardening is in `docs/21` |
| `docs/20-encryption-map.md` | updated | Encrypted-at-rest list gains mail TLS, FreeChat prefs, sealed `.kcert` copies; bearer-credential statement and gap 12 rewritten around owner binding; new gaps 16 (FreeChat membership) and 17 (mail endpoint visibility) |
| `docs/21-live-index-and-keys.md` | updated | Link table: mesh (transcript-bound, pinned peer), direct mail (pinned TLS 1.3), FreeChat; mesh hardening paragraph; status rows |
| `docs/22-wasm-games.md` | ok | Limits table matches `GameLimits` and the website |
| `docs/23-certificate-authority.md` | updated | Owner binding (seal-time and shop-signed), web page passphrase, limits |
| `docs/24-security-audit.md` | updated | Rounds 1–6, with what is fixed and what cannot be |
| Website (`KICKLESpace/index.html`) | updated | Mail and FreeChat cards, claim steps (passphrase), new "Checked in the open" section and nav links, test count 389 |

## Done after the first pass

- `docs/diagrams/encryption-map.svg` and `.png` were redrawn: new wire boxes for direct mail (TLS 1.3, pinned) and FreeChat, the mesh box
  (transcript-bound keys, pinned peer id), the ownership box (owner binding), and the plaintext / never-plaintext / outside-the-boundary lists.
- The website downloads were rebuilt from the current tree: a Release, self-contained single-file Windows exe (smoke-tested: it starts and shows
  its window) and a source archive of the tracked and untracked-but-not-ignored files (no keys, no bundled PHP, no build output), with new
  SHA-256 values on the download cards. The archive contains the website page as it was when the archive was made, so the hash printed inside
  that copy of the page is the previous one; this is unavoidable for a file that lists its own checksum.

## Known leftovers (not fixed)

- The planning documents (`docs/11`, `REPORT.md`) were not re-planned; their phase checklists were only touched where a shipped item is
  ticked in `CHECKLIST.md`.
- The exe is unsigned, so Windows will still say "unknown publisher".
