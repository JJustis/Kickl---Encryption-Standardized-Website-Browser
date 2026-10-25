# 14 — Plugins and the Marketplace

Nickles, the ad client, and anything the community builds are **plugins**. The core stays small; the
economy lives outside it.

## 1. Rules that do not bend

1. A plugin is **untrusted code**. It runs out-of-process, in a sandbox, with declared capabilities.
2. No plugin can read the vault, mail, chat history, identity private keys, cookies, or the registry
   database. Ever.
3. No plugin gets network access by default. Network capabilities are explicit, host-scoped, and shown
   at install.
4. Every plugin is **signed by its author** and **countersigned by the catalog**. Unsigned plugins
   cannot load in normal mode.
5. Plugins are **revocable**: the catalog can publish a revocation that disables the plugin on next
   launch, with a reason shown to the user.
6. The core never auto-updates a plugin without a signature check and a version increase.

## 2. Package format

`name-version.kickl-plugin` = ZIP (stored, no path traversal, ≤ 20 MB):

```
manifest.json          # required, canonical JSON
manifest.sig           # author hybrid signature over SHA-256(manifest.json)
payload/
  main.wasm            # required for logic; ≤ 8 MB
  ui.kml               # optional declarative panel (KML subset)
  assets/…             # ≤ 12 MB, content-addressed
LICENSE
README.md
```

### 2.1 Manifest

```json
{
  "id": "com.example.nickles",
  "name": "Nickles",
  "version": "1.0.0",
  "author": { "name": "…", "address": "kb1q9x7…", "url": "https://…" },
  "description": "Testnet wallet, quote panel, and ad client.",
  "minKickl": "1.0.0",
  "capabilities": [
    "ui.panel",
    "storage.own",
    "payments.quote",
    "payments.send",
    "ads.client"
  ],
  "network": { "hosts": ["api.pinwheel.example"] },
  "entry": "payload/main.wasm",
  "ui": "payload/ui.kml",
  "price": { "mode": "free" },
  "license": "MIT",
  "createdAt": 1792800000
}
```

`price` is one of:

```json
{ "mode": "free" }
{ "mode": "btc", "amount": "0.00025000", "address": "bc1q…" }
{ "mode": "nkl", "amount": 2500 }
```

## 3. Capabilities

| Capability | Grants | Notes |
|-----------|--------|-------|
| `ui.panel` | render `ui.kml` in a dockable panel | KML subset only, no HTML |
| `storage.own` | 64 KB key/value, namespaced, encrypted | wiped on uninstall |
| `clipboard.write` | write text after a user gesture | prompt each time |
| `notifications` | post a native notification | rate-limited |
| `payments.quote` | ask the core for a quote (BTC/NKL) | read-only |
| `payments.send` | request a payment; user confirms in core UI | core renders the confirm, not the plugin |
| `ads.client` | fetch and render the ad slot | only inside the Nickles panel |
| `mail.compose` | open a compose sheet pre-filled | cannot read mail |
| `chat.send` | send a message to an existing thread | user confirms |
| `network.host:<host>` | HTTPS to a declared host | per-host, shown at install |
| `files.export` | save a file via the native dialog | user picks the path |

Denied by default and not requestable: `vault.*`, `mail.read`, `chat.read`, `keys.*`, `registry.write`,
`cookies.*`, `system.exec`, `network.any`.

## 4. Runtime

```
┌──────────── Kickl.Shell (Avalonia) ─────────────┐
│  Plugin host process (separate, sandboxed)      │
│   ├─ WASM engine (Wasmtime) — no WASI by default│
│   ├─ capability broker (IPC, per-call consent)   │
│   └─ panel renderer (KML → Avalonia controls)    │
└─────────────────────────────────────────────────┘
```

- **Windows:** AppContainer + restricted token; job object memory/CPU caps.
- **Linux:** bubblewrap (`--unshare-all`, read-only root, private tmp) + seccomp filter; Flatpak portal
  for file dialogs.
- WASM imports are allowlisted at instantiate time: only the capability ABI. No clock, no random, no
  env, no filesystem unless the capability is granted.
- The panel is KML, rendered by the shell; it cannot draw over the chrome, spawn windows, or capture
  input outside its bounds.
- A plugin crash is contained; the shell shows "plugin stopped" with a restart action.

## 5. Marketplace

### 5.1 Catalog

- Server-side catalog: id, name, version, author address, description, screenshots, price, size,
  capabilities, signature, countersignature, downloads (aggregate), updated_at, status
  (`active`/`revoked`/`delisted`).
- Search is server-side by name/tag (public catalog, no user data in the query beyond the term).
- Detail page shows **capabilities as plain sentences** ("Can ask you to pay for things", "Can talk to
  api.pinwheel.example"), the author's `kb1…` address, the licence, and the signature fingerprint.
- Install flow: download → verify author sig → verify catalog sig → show capability sheet → user
  accepts → unzip to plugin dir → register.
- Updates: user chooses `auto (signed)` or `ask`; a version can raise capabilities, which forces a new
  consent screen.
- Revocation list is fetched with the catalog; a revoked plugin is disabled at next launch and its
  panel shows why.

### 5.2 Pricing and payment

| Mode | Flow |
|------|------|
| Free | direct install |
| BTC | invoice with a unique address (BIP-32 derivation per order), QR + copy, 1 confirmation (0 for < $5 with Lightning later), license issued on confirmation |
| NKL | testnet invoice; instant, but labelled `TESTNET — NO VALUE` |

**License token** (offline-verifiable):

```json
{
  "v": 1,
  "plugin": "com.example.nickles",
  "version": "1.0.0",
  "buyer": "kb1q9x7…",
  "order": "ord_01J…",
  "issuedAt": 1792800000,
  "price": { "mode": "btc", "amount": "0.00025" },
  "sig": "b3373-byte-catalog-signature"
}
```

Stored in `plugins.db`; the client verifies the catalog signature and the buyer address. Offline use
is allowed for 30 days without a re-check; after that, a cached revocation check is attempted, and
failure to reach the catalog does **not** disable a paid plugin (no ransom by outage).

**Custody:** in 1.0 payments go **directly to the seller's address**. Kickl does not hold funds, which
keeps us out of money transmission but means refunds are the seller's responsibility. Revenue split
is Q-04; default 100% seller.

### 5.3 Reviews and ranking

- Reviews require a purchased license or a 7-day-old identity; one review per identity per plugin.
- Ranking: `0.5*log(downloads) + 0.3*rating + 0.2*recency`, with a manual `featured` flag.
- No paid placement in the catalog. Paid placement exists **only** in the ad slot (`docs/15`).

## 6. The Nickles plugin (first-party, optional)

```
id: com.kickl.nickles
capabilities: ui.panel, storage.own, payments.quote, payments.send, ads.client, notifications
```

- **Quote panel ("qt")**: when opened, it shows the current NKL/BTC quote, fee, and a `Buy / Send /
  Receive` set of actions. The core renders the confirmation; the plugin never sees a seed or key.
- **Wallet**: balance, receive address, history, faucet (testnet), chain status.
- **Ad client**: requests the slot manifest, renders a static creative inside the panel, reports
  nothing.
- Cannot run unless the user enables it during onboarding or installs it later.
- Removal is clean: balances remain on-chain; local history is exported or deleted with the plugin
  data.

## 7. Review and security process

| Step | What |
|------|------|
| Automated | manifest schema, signature, capability allowlist, wasm import scan, zip-bomb ratio, size caps, string scan for URLs not declared |
| Manual (first release) | source or wasm review for high-risk capabilities (`payments.*`, `network.*`, `chat.send`) |
| Sandbox test | run with network off, attempt filesystem/key access, confirm denial |
| Publish | countersign, catalog entry, changelog |
| Post-publish | crash reports (opt-in), revocation if abused |

High-risk plugins get a visible badge and a slower review. A plugin that asks for `payments.send` and
`network.any` together is rejected by policy.

## 8. Threat model (plugin-specific)

| Threat | Control |
|--------|---------|
| Malicious plugin steals keys | no key capability exists; separate process; no memory sharing |
| Plugin exfiltrates mail | no mail read capability; network host allowlist |
| Supply-chain update | author + catalog signatures; version monotonic; revocation |
| Sandbox escape | OS sandbox (AppContainer/bubblewrap) + seccomp + minimal WASM imports |
| UI spoofing | panel bounded; native confirm dialogs for payments/consent |
| Payment fraud | unique invoice, confirmation policy, license bound to buyer address |
| Catalog compromise | countersigning key offline/HSM; clients pin the catalog key; revocation feed signed |
| Plugin DoS | CPU/memory caps, watchdog, kill switch |

## 9. UX copy rules

- Never say "safe plugin". Say "**Signed by X · can talk to Y · cannot read your mail**".
- The capability sheet is a checklist, not a wall of text.
- Uninstall asks: keep data (export) or delete it.
- A revoked plugin explains exactly which advisory caused it and links to the advisory page.

## 10. Implementation checklist (2026-10-25)

- [ ] Plugin process/runtime, WASM host, and OS sandbox (AppContainer/bubblewrap).
- [ ] Manifest/package validation, author and catalog signatures, capability broker, and revocation.
- [ ] Marketplace catalog, install/update/remove flow, reviews, licensing, and offline verification.
- [ ] First-party Nickles plugin, including a wallet boundary that never exposes seed/private keys.
- [ ] Network-off, filesystem-denial, key-isolation, resource-cap, and sandbox escape verification.

The repository currently contains no plugin or marketplace runtime. These requirements remain a
design specification, not shipped behavior.
