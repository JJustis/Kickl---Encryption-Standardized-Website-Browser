# 16 — Input Privacy, Session Gate, and Per-Identity Data

## 1. What this layer defends against

| Threat | Defended? | How |
|--------|-----------|-----|
| A log/crash dump containing what you typed | **yes** | keystrokes never stored as text; sealed cells only |
| A memory scraper reading a `string` in the heap | **partly** | sealed cells + pinned buffers + zeroisation; paint buffer still exists |
| Mouse heatmaps / click analytics | **yes** | no pointer storage, no analytics, no telemetry |
| Device fingerprinting by our server | **yes** | no device fields collected or stored |
| Device fingerprinting by a sealed site | **yes** | no script on the sealed surface |
| Device fingerprinting by a clearnet site | **partly** | engine protections + disposable randomized profile; the site still sees IP/TLS |
| Another local identity reading your data | **yes** | per-identity encrypted profile roots |
| Someone using your open session | **yes** | session gate, auto-lock, short-lived session key |
| A kernel-level keylogger / screen capture / camera | **no** | out of scope; stated in the UI |
| A compromised OS with admin | **no** | out of scope |

## 2. The entry gate

```
┌──────────────────────────────────────────────────────────────┐
│                        KICKL                                 │
│                                                              │
│   This device has no active session.                         │
│                                                              │
│   ┌───────────────────────┐   ┌──────────────────────────┐   │
│   │  Sign in              │   │  Not registered          │   │
│   │  restore your identity│   │  create a new identity   │   │
│   └───────────────────────┘   └──────────────────────────┘   │
│                                                              │
│   user key:  [ •••••••• ]        [ use security key ]        │
│                                                              │
│   ⓘ Your key never leaves this device. There is no reset.    │
└──────────────────────────────────────────────────────────────┘
```

- **Sign in**: the current browser shell unlocks the local profile with its user key.
- **Not registered**: the current shell can create an encrypted local profile with a user key.
  The UI returns to the locked gate after creation; the user signs in as a separate explicit step.
  Identity selection is currently local profile selection, not portable identity restore. Recovery
  phrase setup and hardware-backed sign-in are still unimplemented.
- The **user key** is one of:
  - PIN/passphrase (Argon2id → KEK), or
  - a FIDO2 security key (hmac-secret / resident credential) **plus** a PIN, or
  - TPM-bound key with Windows Hello / Linux polkit prompt.
- Failure throttling: 5 attempts → 30 s, 10 → 5 min, then a 1 h cool-down; the UI shows remaining
  attempts, not whether the key was "close".
- No "forgot key" link. There is a recovery-phrase path for the identity, but the vault/mail keys are
  gone if the user key and recovery material are both lost. This is stated before creation.
- **Stay unlocked** (optional): max 12 h, revoked on OS lock, on sleep, on panic, and on identity switch.

## 3. Session secret

```
sessionNonce = 32 random bytes (per unlock)
sessionKey   = HKDF-SHA-512(vaultMaster, salt=sessionNonce, info="kickl/v1/session", 32 bytes)
sessionId    = BLAKE3(sessionKey)[0..16]
```

- Stored in a `SecureBuffer` (pinned, `VirtualLock`), zeroised on lock, exception, suspend, and identity
  switch.
- Never written to disk, never sent to the server, never included in crash reports.
- Rotated on every unlock, on resume from sleep, and every 12 h.

## 4. Per-keystroke cells

```csharp
sealed class KeyCell {          // one character
    ulong Counter;              // monotonic within the field
    byte[] Nonce;               // 12 bytes: counter, big-endian, padded
    byte[] Ciphertext;          // 1–4 bytes + 16-byte tag
}
```

- `ct_i = AES-256-GCM(sessionKey, nonce=counter_i, pt=UTF-8(char_i),
  aad=sessionId || fieldId || counter_i)`
- Each cell binds the field and position, so cells cannot be reordered, spliced into another field, or
  replayed.
- Editing (backspace, selection, paste) rewrites or removes cells; the counter never reuses a nonce.
- Reveal is explicit and short-lived: `using var text = field.Reveal()` → `Span<char>` in a pinned,
  zeroised buffer. The painter writes it to the text layout and clears it on the next frame.
- **Paste** is treated as a bulk import: the clipboard string is read once, sealed cell-by-cell, and
  the source string is cleared where the platform allows.
- Fields that are *not* sensitive (a game's name input, a clearnet page's form) may use the normal
  engine path; the shield is applied to omnibox, search, PIN, mail/chat compose, and note fields.
- **IME**: for sensitive fields, disable IME composition logging and inline learning; the composition
  string is sealed on each update. Complex-script IMEs may need a documented exception; if so, the
  field shows a small `IME` badge so the user knows the shield is reduced.

## 5. Mouse and pointer

- Pointer events are handled for paint/hover only. There is **no** pointer log, no click map, no dwell
  time, no scroll-depth analytics.
- Sealed documents cannot subscribe to pointer events (no script).
- The clearnet mini-browser uses the engine's default event model but has no bridge to Kickl; we do not
  inject analytics.
- A code-level guard: `PointerEvent` types are not accepted by the `SafeLog` API, and a unit test
  reflects over the logging surface to assert no pointer type is loggable.
- Cursor position is never included in diagnostics bundles.

## 6. Device-query ban

**Client:** no collection of hardware IDs, serial numbers, MAC addresses, disk IDs, battery state,
sensor data, installed fonts, or device names. The only local identifiers are cryptographic
(`identityId`, `deviceId` = hash of public keys) and they are never sent in cleartext to the registry
or relay (device auth uses signatures; presence uses rotating peer ids).

**Server:** the schema has no column for a device fingerprint, user agent, screen size, locale, or IP
history. Aggregate counters only. Request logs for mail/relay paths are not written.

**Clearnet:** the mini-browser uses a fresh disposable profile per window, and the engine's
fingerprinting protections where available; the sealed surface cannot query the device at all.

**Honest limit:** a clearnet site still sees your IP and TLS metadata, and can fingerprint the engine
version. Use the system browser with its own protections if that matters.

## 7. Per-identity profile roots

```
%LOCALAPPDATA%\Kickl\            (Linux: ~/.local/share/kickl/)
├─ profiles\
│   ├─ <identityId>\            one per identity; encrypted
│   │   ├─ identity.db
│   │   ├─ vault\
│   │   ├─ mail.db
│   │   ├─ chat.db
│   │   ├─ cookies.db
│   │   ├─ history.db
│   │   ├─ sessions\            tab state, window state
│   │   └─ keys\                wrapped vault key, device certs
│   └─ …
├─ shared\
│   ├─ registry.db              public signed snapshots (no user data)
│   └─ updates\
└─ logs\                        no identity content
```

- `identityId = BLAKE3(identity public bundle)[0..16]`, so the directory name reveals nothing usable.
- Switching identity closes all windows, zeroises the session key, and mounts the other profile root.
- The registry cache is shared because it is public data; per-identity pins and visit history are not.
- Uninstalling/panic-wipe can target one identity or all.

## 8. UI states

| State | UI |
|-------|-----|
| No session | entry gate, no chrome |
| Unlocking | spinner while deriving the versioned Argon2id key; duration depends on device performance |
| Unlocked | normal chrome; a small `session` chip shows remaining auto-lock time |
| Locked mid-session | blurred viewport, "Locked" panel, PIN to resume |
| Identity switch | full-screen wipe animation, then entry gate |
| Key lost | explicit dead-end screen with recovery options; no fake "reset" |

## 9. Tests

- [ ] Same character typed twice produces different ciphertext (nonce + AAD).
- [ ] Cells cannot be reordered or moved to another field (tag failure).
- [ ] `Lock()` zeroises the session key; `Reveal()` throws afterwards.
- [ ] Encoded field buffer does not contain the typed substring (byte scan).
- [ ] No pointer type is loggable (reflection test).
- [ ] Profile root A cannot be opened with identity B's key.
- [ ] Wrong user key fails closed; throttle counters persist across restarts (encrypted).
- [ ] Crash-dump config is off; diagnostics preview contains no text.
- [ ] Sealed document has no pointer/script access (corpus).
