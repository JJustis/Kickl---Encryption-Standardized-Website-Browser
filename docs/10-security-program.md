# 10 — Security Program

This is the document that answers "what safety protocol is taken while construction commences, and
what do we do about CVEs". It is a working program, not a policy statement.

## 1. Assets

| Asset | Where it lives | Impact if lost |
|-------|----------------|----------------|
| Identity private keys | device keystore, TPM-sealed | impersonation, permanent identity theft |
| Vault master + DEKs | TPM/DPAPI + encrypted DBs | all saved pages and mail readable |
| Mail/chat plaintext | RAM only (disk is ciphertext) | conversation disclosure |
| Registry snapshot | local SQLCipher + public server | resolution poisoned if signatures bypassed |
| Update signing key | offline/HSM | remote code execution for every user |
| Server DB | operator host | metadata + ciphertext; availability |
| Build pipeline | CI + developer machines | supply-chain compromise |

## 2. Adversaries and what we can/cannot do

| Adversary | Capability | Our mitigation | Residual risk |
|-----------|-----------|----------------|---------------|
| Passive network | observes traffic | TLS 1.3 + Noise + HPKE | metadata (IP, timing, size) |
| Malicious relay | stores/forwards, drops | E2E encryption, signed envelopes, multiple relays | traffic analysis, DoS |
| Malicious registry operator | can withhold/equivocate | signatures, Merkle proofs, gossip, TOFU pins | eclipse of a new user; withholding |
| Malicious domain owner | serves hostile markup/blobs | sanitizer, CSP, no JS, blob isolation | zero-days in decoder, social engineering |
| Compromised peer | sends malicious content | same as above + signature checks | endpoint compromise |
| Local thief (device stolen) | has disk | TPM + Argon2id + PIN | weak PIN, cold-boot, TPM attacks |
| Local malware / admin | full OS access | **cannot defend**; we minimise footprints | total compromise |
| Coercion | legal/physical | panic/duress (limited), no backdoor | cannot be solved technically |
| Supply chain attacker | malicious dependency/update | pinning, SBOM, signatures, review | sophisticated CI compromise |
| Forensic imaging | copies disk | crypto-erase, no plaintext at rest | backups, VSS, pagefile, SSD remanence |

**We publish §2 verbatim in the app's security page.** No absolute claims.

## 3. Threat model (per feature, at design time)

Every new feature touching keys, parsing, network, or the DOM gets a one-page model with:

```
Feature:
Assets:
Trust boundaries:
Attacker capabilities:
Abuse cases (≥ 5):
Mitigations:
Residual risk + owner:
Tests that prove it:
```

Stored in `docs/threat/<feature>.md`. A PR template requires the link. No model → no merge for
security-sensitive code.

## 4. Attack surface inventory (1.0)

| Surface | Exposure | Primary controls |
|---------|----------|------------------|
| KML parser | untrusted documents | strict parser, allowlist, limits, fuzz |
| HTML sanitizer | decrypted blobs | allowlist, CSP, no JS, corpus tests |
| OSS compiler | untrusted CSS | grammar allowlist, no `url()` external |
| Blob container parser | untrusted files | bounds checks, AEAD before parse, pixel caps |
| PNG/WIC decode | untrusted images | OS decoder + limits, isolated process where possible |
| CBOR decoder | network + disk | depth/size limits, no polymorphic types |
| Noise/HPKE/ratchet | network | spec + vectors + fuzz + audit |
| Registry verifier | network | signatures, Merkle, monotonicity |
| IPC | local same-user | ACL, token, size caps, schema validation |
| WebView2 bridge | page → app | narrow message schema, no eval, no host objects |
| Clearnet WebView2 | internet | JS off, sandbox, disposable profile, no permissions |
| Updater | network | signature, rollback guard, staged rollout |
| PHP API | internet | prepared statements, auth, rate limits, WAF |
| MySQL | localhost | least privilege, no network exposure |
| Installer | download | Authenticode + MSIX signing |

## 5. CVE program — "look for CVEs and patch where we can"

### 5.1 What we actually control

| Component | Can we patch it? | What we do |
|-----------|------------------|------------|
| Our C# code | **yes** | fix, test, ship |
| Our PHP/JS/HTML server code | **yes** | fix, deploy, rotate |
| NuGet dependencies | usually no | upgrade/fork/patch + upstream report |
| WebView2 / Chromium | **no** | track MSRC/Chrome releases, enforce minimum version, force runtime update, kill switch |
| Windows / .NET runtime | no | required OS updates, target latest LTS, mitigations |
| PHP / MySQL / nginx | no (we deploy) | patch within SLA, maintenance windows |
| libsodium / OpenSSL | no | upgrade, monitor advisories |

**"Patch where you can" is therefore a triage rule, not a slogan:** fix our code immediately; upgrade
or fork dependencies within SLA; for Chromium/OS, shorten exposure and communicate.

### 5.2 Sources (checked automatically, daily)

- Microsoft Security Response Center (MSRC) API + WebView2 release notes
- Chrome Releases blog + Chromium issue tracker (security)
- GitHub Advisory Database + Dependabot alerts
- OSV.dev (ecosystem-wide)
- NVD CVE feed (keyword: webview2, .NET, WPF, PHP, MySQL, nginx, libsodium, AngleSharp, BouncyCastle)
- PHP.net security releases, MySQL CPU advisories, nginx advisories
- Vendor advisories for every direct dependency (recorded in `deps.json`)

### 5.3 Severity and SLA

| Severity | Definition | Triage | Fix/mitigate | Disclosure |
|----------|-----------|--------|--------------|------------|
| **S0 Critical** | RCE, key exfiltration, auth bypass, updater compromise, plaintext leak at rest | 1 h | 24 h (workaround) / 72 h (release) | advisory + forced update |
| **S1 High** | sandbox escape, XSS with app privileges, signature bypass, DoS of core | 4 h | 7 days | advisory |
| **S2 Medium** | limited info leak, local DoS, metadata amplification | 1 day | 30 days | release notes |
| **S3 Low** | hardening gap, best-practice deviation | 3 days | next release | release notes |
| **S4 Info** | no security impact | — | backlog | — |

A missed SLA is an incident, logged publicly in the security changelog with the reason.

### 5.4 Triage runbook

```
1. Ingest alert → create SEC ticket with CVE/advisory id, CVSS, exploit status, affected versions.
2. Reproduce or prove not-affected (record the evidence).
3. Classify with §5.3; assign owner; set deadline.
4. If exploitable in the wild: enable kill switch / disable feature remotely (signed config).
5. Fix on a private branch; add a regression test that fails before the fix.
6. Review by second engineer; crypto changes need the crypto reviewer.
7. Build signed release; stage 5% → 25% → 100%; watch crash/telemetry-free error counters.
8. Publish advisory (affected versions, impact, workaround, fixed version, credit).
9. Post-incident review within 5 days; update threat model and checklist.
```

### 5.5 Dependency intake rules

A new package is rejected unless all are true:

- [ ] Licence compatible (MIT/BSD/Apache/ISC; no GPL in the shipped binary without legal review).
- [ ] Maintained in the last 12 months, or we accept ownership of a fork.
- [ ] No open Critical/High advisory.
- [ ] Transitive tree inspected (no unexpected native binaries, no post-install scripts).
- [ ] Version pinned exactly; lock file committed; hash verified.
- [ ] Named maintainer and a removed-if-abandoned plan.
- [ ] Recorded in `deps.json` (name, version, licence, source, why).

Banned: `BinaryFormatter`/`NetDataContractSerializer`, `System.Web`, `eval`-style dynamic compilation
of untrusted input, packages that phone home, obfuscated packages, packages with install scripts that
download binaries.

### 5.6 What we will not do

- No custom crypto to "fix" a CVE.
- No silent downgrade or "ignore certificate" escape hatch.
- No remote debugging in release builds.
- No unsigned update path, ever, not even for "emergency" fixes.
- No security-through-obscurity claims about the protocol.

## 6. Windows hardening baseline

| Control | Implementation |
|---------|----------------|
| Code integrity | Authenticode EV + MSIX; `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)`; no DLLs from CWD |
| Mitigations | `SetProcessMitigationPolicy`: CFG, DEP, ASLR (system), SEHOP, strict handle checks, blocking legacy extension points, font loading restricted |
| ACG / CIG | enabled where compatible (may conflict with WPF/JIT; test per build) |
| CET / shadow stack | opt-in via manifest, test on supported CPUs |
| AppContainer | WebView2 already runs renderers sandboxed; clearnet profile additionally network-isolated by policy |
| Token | standard user; no admin manifest; installer elevates once |
| Temp files | `%LOCALAPPDATA%\Kickl\tmp`, ACL current user, cleaned on start/exit |
| Secrets | DPAPI-NG/TPM; never in registry; never in env vars; never command line |
| Crash dumps | disabled by default; opt-in local-only; secrets zeroised |
| DevTools | compiled out of Release via `#if DEBUG`; `KML_DEV=1` requires a signed debug build |
| Clipboard | optional auto-clear after 30 s for copied addresses; never copy keys |
| Screen capture | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` when privacy mode is on (best effort) |
| Autoplay / media | blocked on sealed surface; clearnet requires a gesture |
| Network | no inbound listener; outbound allowlist by host in the clearnet profile where feasible |

## 7. Verification program

| Type | Tool / method | Gate |
|------|---------------|------|
| Unit + property tests | xUnit, FsCheck | every PR |
| Crypto vectors | ACVP, Wycheproof, RFC 9180, Noise, Signal, MLS | G2 |
| Fuzzing | SharpFuzz (parser, CBOR, container, KMLBOX) 24 h in CI nightly | G2, G4, G7 |
| Static analysis | Roslyn analyzers, CodeQL, Semgrep, PHPStan/Psalm L8 | every PR |
| Dependency | NuGet audit, `composer audit`, OSV scanner, Dependabot | daily |
| Secret scanning | gitleaks + GitHub secret scanning | every push |
| DAST | OWASP ZAP against the PHP API | nightly |
| Web attack corpus | 500+ XSS/payload cases | G4 |
| Forensic test | write data → wipe → image → strings/grep | G7, G10 |
| Penetration test | external firm, grey-box (API + client) | G12, pre-1.0 |
| Crypto audit | external firm, design + implementation | G2, G14 |
| Red team | social engineering + malware simulation, tabletop | pre-1.0 |
| Availability | load test 10× expected peak, chaos (DB down, disk full, clock skew) | G12 |

## 8. Disclosure and incident response

- `security.txt` at `https://<host>/.well-known/security.txt` with a PGP key, 90-day disclosure
  window, safe-harbour statement.
- Private advisory channel (GitHub Security Advisories) for client and server.
- Bug bounty: start with a private program and a small budget; publish scope and out-of-scope
  (physical, DoS at scale, social engineering, third-party sites).
- Incident levels: SEV1 (keys/updater/RCE), SEV2 (data exposure/availability), SEV3 (limited).
- Comms template: what happened, who is affected, what we did, what you should do, timeline, next
  update time. No speculation, no blame.
- Key compromise playbook: revoke device certs, rotate registry key, publish new snapshot, force
  client update, invalidate mailbox tokens, notify contacts.
- Post-incident: blameless review, action items with owners and dates, public summary for SEV1/2.

## 9. Privacy program

| Principle | Implementation |
|-----------|----------------|
| Data minimisation | no account, no email required, no analytics SDK |
| Purpose limitation | data collected only for the feature that needs it |
| Local-first | registry, contacts, search, vault all local |
| No telemetry by default | opt-in crash reports only, previewed before send |
| No third-party SDKs | none in the client; server uses only PHP/MySQL |
| Retention | logs 7 days, mail 30 days default, presence 120 s |
| User access/deletion | export + panic wipe covers everything |
| Children / sensitive data | not directed at children; no age data collected |
| Transparency | this document + in-app security page + changelog |

## 10. Security gates by phase

| Gate | Must be true |
|------|--------------|
| G1 | CI enforces analyzers, tests, SBOM, secret scan; signing works |
| G2 | Crypto core passes all vectors; fuzz clean 24 h; design reviewed externally |
| G3 | No UI thread secret handling; IPC schema validated; fuzz on IPC |
| G4 | XSS corpus 0 bypasses; CSP verified; no network from sealed surface |
| G5 | Tamper/replay/rollback/equivocation tests pass; pinning works |
| G6 | Sealed-at-rest grep test passes; PIN throttling + TPM path tested |
| G7 | Container fuzz + torn-write + forensic test pass |
| G8 | Server cannot decrypt mail (test with a packet capture + DB dump) |
| G9 | Ratchet/MLS FS/PCS tests pass; replay rejected |
| G10 | Crypto-erase verified on a real SSD + VM image |
| G11 | Clearnet JS-off default; sandbox escape test suite; profile destruction verified |
| G12 | Pentest closed; load test passes; backups restore in < 1 h |
| G13 | Legal sign-off; testnet flag cannot be enabled in release |
| G14 | External audit findings closed; disclosure policy live; incident drill done |

## 11. Open security questions (track until answered)

1. Does .NET 10 expose ML-KEM/ML-DSA with the exact API we need, on all supported OS versions?
   (Fallback: BouncyCastle 2.6+; decision at G0.)
2. Which MLS implementation do we trust enough to ship? (Evaluate at least two; audit required.)
3. Can we run the vault decoder in a separate low-integrity process? (Investigate AppContainer.)
4. Does `WDA_EXCLUDEFROMCAPTURE` behave acceptably with WebView2 composition? (Test G3.)
5. What is the real-world cost of Argon2id 64 MiB on the minimum supported hardware? (Benchmark G2;
   lower to 32 MiB only with a documented reason.)
6. Can we delete WebView2 profile data reliably on all supported versions? (Test G11.)
7. Is TPM sealing available and stable on Windows 10 22H2 without a Microsoft account? (Test G2.)


## Status note (2026-10-27)

The implementation has had an in-house security audit in three rounds; findings, fixes and the list of things that
remain open or cannot be fixed by the author are in `docs/24-security-audit.md`. An independent penetration test is
still outstanding and is the one item only an outside party can supply.
