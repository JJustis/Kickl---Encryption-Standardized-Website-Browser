# 02 — Architecture

## 1. Process and trust boundaries

```
┌──────────────────────────────┐        ┌────────────────────────────────────┐
│ Kickl.Shell.exe (WPF)         │        │ Kickl.Core.exe (console-less)       │
│  UI, animations, tabs, dock  │◄──────►│  keys, crypto, vault, registry,    │
│  WebView2 A (sealed)         │  ALPC  │  mail, chat, transport, sanitizer  │
│  WebView2 B (clearnet modal) │ named  │  no listening socket               │
│  no long-term keys           │ pipes  │  no UI                             │
└──────────┬───────────────────┘        └──────────────┬─────────────────────┘
           │ WebView2 process tree                      │ outbound TLS 1.3
           │ (Chromium sandbox, per-site)               │
           ▼                                            ▼
      renderers, GPU, network                   Pinwheel service
```

**Rules**
- The shell never holds the identity private key or vault key. It receives *derived capabilities*
  (e.g. "decrypt blob B for 5 minutes").
- The core never parses untrusted HTML. It parses KML markup and sanitises; the shell injects only
  sanitised fragments.
- All cross-process messages are length-prefixed CBOR over a named pipe with a per-boot random pipe
  name, ACL restricted to the current user SID, plus a 32-byte session token. Max message 8 MiB.
- Secrets never cross the boundary in logs or crash reports.

## 2. Solution layout

```
Kickl Browser.sln
├─ src/
│  ├─ Kickl.Shell/                 WPF app; views, view-models, animation, docking
│  │   ├─ Chrome/                custom window chrome, rail, tab strip
│  │   ├─ Omnibox/               parsing, suggestion engine, scheme rewriting
│  │   ├─ Surfaces/              WebView2 hosts, CSP injection, bridge
│  │   ├─ Dock/                  chat + mail lobes, popups, notifications
│  │   ├─ Vault/                 gallery, viewer, unlock dialogs
│  │   └─ Themes/                tokens, hacker theme, high-contrast
│  ├─ Kickl.Core/                  service host, IPC, orchestration
│  ├─ Kickl.Crypto/                KEM/AEAD/KDF/ratchet/HPKE wrappers, keystore
│  ├─ Kickl.Vault/                 containers, thumbnails, ratchet, forensic guards
│  ├─ Kickl.Registry/              snapshot verify, Merkle proofs, local mirror
│  ├─ Kickl.Identity/              addresses, device certs, recovery, contacts
│  ├─ Kickl.Mail/                  store, compose, queue, attachments
│  ├─ Kickl.Chat/                  Double Ratchet, MLS wrapper, presence
│  ├─ Kickl.Transport/             Pinwheel client, Noise, HTTP, retry, jitter
│  ├─ Kickl.Markup/                KML parser, OSS compiler, sanitizer, CSP builder
│  ├─ Kickl.Clearnet/              modal browser policy, profile lifecycle
│  ├─ Kickl.Nickles/               testnet ledger client (isolated assembly)
│  ├─ Kickl.Update/                signed manifests, staging, rollback guard
│  └─ Kickl.Shared/                primitives, CBOR models, result types, logging
├─ tests/
│  ├─ Kickl.Crypto.Tests/          ACVP + Wycheproof + property tests
│  ├─ Kickl.Markup.Tests/          500-vector XSS suite + angle-bracket fuzz
│  ├─ Kickl.Registry.Tests/        tamper, replay, rollback, Merkle proofs
│  ├─ Kickl.Integration.Tests/     two-process end-to-end, offline, wipe
│  ├─ Kickl.Fuzz/                  SharpFuzz targets (parser, CBOR, container)
│  └─ Kickl.Perf/                  BenchmarkDotNet + startup traces
├─ server/                        PHP 8.4 reference service + migrations + tests
├─ tools/                         signing, SBOM, release, vector generation
└─ docs/
```

**Implemented layout (2026-10-25).** The tree above is the target decomposition. The running slice is
`src/Kickl.Browser` (Avalonia shell), `src/Kickl.Core` (crypto, markup, registry, session), `src/Kickl.Cli`,
`src/Kickl.Device`, `src/Kickl.DongleReceiver`, `tests/Kickl.Core.Tests`, `tools/Kickl.RegistrySigner`,
and `server/` (PHP 8.2 + MariaDB registry subset). Domain code lives in `Kickl.Core`; the separately
named domain projects above have not been split out yet.

**Dependency direction:** Shell → Shared; Shell → (IPC contract only). Core → all domain libs.
`Kickl.Core` uses BCL cryptography plus the pinned Bouncy Castle package for Argon2id. `Kickl.Nickles` depends on nothing
(so it can be excluded from the installer).

## 3. Key flows

### 3.1 Navigate to a sealed name
```
omnibox "nyx.custom/research"
  → OmniboxParser: normalise (IDN, lowercase, trailing dot, percent-encoding)
  → RegistryMirror.Resolve("nyx.custom")
      local snapshot (verified at ingest) → record {ownerKem, ownerSig, endpoints, epoch}
      miss → show "not in phonebook" (never query the server for a single name)
  → Surface.Navigate("jttp://nyx.custom/research")
  → Transport.FetchSealed(record, path)
      ephemeral X25519+ML-KEM → HPKE seal request → owner relay/peer
      response: signed KML document, blobs as ciphertext
  → Markup.Parse + Sanitize → CSP → inject skeleton (sealed placeholders)
  → user clicks blob → Core.UnlockBlob(blobId) → PIN-gated DEK → plaintext fragment
  → Shell replaces placeholder with sanitised fragment in a locked iframe
```

### 3.2 Send mail
```
compose → Kickl.Mail.Build:
  subject/body → CBOR → per-message key (optional) → HPKE to recipient KEM
  → envelope {to: mailboxHash, ct, sig, ts} → outbox (encrypted SQLite)
  → Transport.Post("/v1/mail") with Noise channel + TLS
  → 202 accepted → mark queued → poll delivery receipts
```

### 3.3 Registry sync
```
every 60–300 s (jitter, backoff on error):
  GET /v1/registry/head → {version, rootHash, sig, keyId}
  if version > local: GET /v1/registry/snapshot?from=<v> → delta + new root
  verify: hybrid signature, Merkle root over sorted domain records, monotonic version,
          no rollback, no equivocation vs. transparency log
  atomically swap mirror (SQLite transaction) → invalidate resolution cache
```

## 4. Storage layout

```
%LOCALAPPDATA%\Kickl\
├─ identity.db        SQLCipher; identity public bundle, device certs, contacts (encrypted)
├─ vault\             *.kickl.png containers (ciphertext + thumbnail), index.db
├─ mail.db            SQLCipher; envelopes, bodies (ciphertext), outbox, receipts
├─ chat.db            SQLCipher; sessions, ratchet state, message ciphertext
├─ registry.db        SQLCipher; signed snapshots, local resolution cache
├─ cookies.db         SQLCipher; per-partition jar
├─ keys\
│   ├─ vault.key      DPAPI-NG / TPM-sealed, ACL current user
│   └─ devices.json   public only
├─ webview\           WebView2 user data folders (sealed profile, clearnet profile)
├─ logs\              structured, redacted, rotated, 7-day default retention
└─ updates\           staged packages, verified
```

- **SQLCipher 4** with per-database keys derived from the master key via HKDF with distinct labels.
- `PRAGMA cipher_memory_security = ON`, `secure_delete = ON`, WAL enabled but WAL also encrypted by
  SQLCipher; checkpoint on lock.
- Thumbnails are the only intentionally plaintext-ish artefact; they are coarse (≤ 320 px, blurred
  8 px, no text) or user-disabled. Document this.
- WebView2 profiles: sealed profile has cache disabled (`--disk-cache-size=0`), no autofill, no
  password store, no media cache; clearnet profile is deleted on modal close.

## 5. Concurrency model

| Concern | Model |
|---------|-------|
| UI | WPF dispatcher; no I/O on UI thread |
| Crypto | bounded worker pool (Environment.ProcessorCount−1), `Task` with cancellation |
| Network | one `HttpClient` per profile with SocketsHttpHandler; HTTP/2; no connection pooling across identities |
| Mail/chat | actor-per-conversation (`Channel<T>`), serialised per session to protect ratchet state |
| Registry sync | single-flight background service; `PeriodicTimer` with jitter |
| SQLite | one writer connection per database, WAL readers; `SemaphoreSlim` per DB |
| Panic | cancellation token source "global kill" checked at every await boundary |

## 6. Error handling and UX states

- **No silent failure.** Every network/crypto error maps to a user-visible state with a stable code
  (`E-CRYPTO-004`) and a "what happened / what to do" pair.
- **Fail closed.** Signature, Merkle, or transcript-hash failure never degrades to "show anyway".
- **Offline is a first-class state.** Cached sealed pages open; mail queues; chat shows "queued".
- **Retry policy:** exponential backoff 2 s → 5 min, jitter ±30%, max 12 attempts, then surface.
- **Circuit breaker** per endpoint; registry failure never blocks local browsing.

## 7. Logging and diagnostics

- Structured JSON lines, local only. Fields: timestamp, level, component, code, correlation id.
- **Never logged:** plaintext, keys, PINs, full addresses, message bodies, domains visited.
- Redaction is enforced by an allowlist serialiser (`SafeLog`), not by regex scrubbing.
- User can export a diagnostics bundle (logs + versions + config with secrets removed) after a
  preview screen showing exactly what will be included.
- Crash dumps: disabled by default (`WerRegisterRuntimeExceptionModule` opt-out); if enabled, dumps
  are local-only and secrets are zeroised on crash where possible.

## 8. Standards and compliance mapping

| Area | Standard | Where enforced |
|------|----------|----------------|
| URLs | WHATWG URL, RFC 3986 | `OmniboxParser`, custom scheme registration |
| Scheme | WebView2 `CoreWebView2CustomSchemeRegistration` (`TreatAsSecure`, standard) | Surface init |
| Transport | TLS 1.3, HTTP/2, RFC 9110/9111 | `Kickl.Transport` |
| Cookies | RFC 6265bis semantics, SameSite=Lax default | `Kickl.Cookies` |
| Web security | CSP L3, `sandbox`, SRI, Trusted Types where available | `Kickl.Markup` CSP builder |
| Crypto | FIPS 203/204/205, RFC 9180, 8439, 8452, 9106, 5869 | `Kickl.Crypto` |
| Messaging | Signal Double Ratchet, MLS RFC 9420 | `Kickl.Chat` |
| Encoding | CBOR RFC 8949, COSE RFC 9052, Bech32m BIP-350 | `Kickl.Shared` |
| Transparency | RFC 6962 (Merkle log) | `Kickl.Registry` |
| Accessibility | WCAG 2.2 AA, UIA | `Kickl.Shell` |
| Supply chain | CycloneDX 1.5, SLSA-style provenance | CI |
| Disclosure | RFC 9116 `security.txt` | server + repo |

## 9. Performance budgets

| Operation | Budget (p95) | Instrumentation |
|-----------|--------------|-----------------|
| Process start → window visible | 800 ms | ETW `Kickl/Startup` |
| Window visible → interactive | 1.5 s | marker |
| Omnibox suggestion list | 30 ms after keystroke | stopwatch |
| Tab create | 150 ms | marker |
| Registry verify (10k domains) | 120 ms | benchmark |
| Blob decrypt (64 KiB) | 8 ms | benchmark |
| Vault thumbnail list (200 items) | 200 ms virtualised | benchmark |
| Mail sync (100 headers) | 400 ms | integration |
| Panic crypto-erase | 500 ms to unrecoverable | integration test |

## 10. Build, packaging, release

- `dotnet build -c Release`, `PublishReadyToRun`, `PublishSingleFile=false` (WebView2 loader),
  `InvariantGlobalization=false`, `ILCompiler` not used (reflection in WPF).
- MSIX package, x64 + arm64, signed with EV certificate; app alias `kickl`.
- WebView2: Evergreen runtime with a **minimum version floor** checked at startup; if below floor,
  show a blocking update prompt with a link — never silently run an old Chromium.
- Update manifest: JSON, signed with Ed25519+ML-DSA-65; includes version, hashes, minimum OS,
  rollout percentage, kill-switch flag.
- Reproducible-ish build: deterministic flags where possible, `SOURCE_DATE_EPOCH`, hashes published.
- CI gates: build, analyzers (`TreatWarningsAsErrors`), unit tests, crypto vectors, XSS corpus,
  SBOM diff, secret scan, dependency review, licence check, signed-artefact check.
