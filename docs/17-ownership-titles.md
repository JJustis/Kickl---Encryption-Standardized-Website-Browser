# 17 — Ownership Titles, the Badge, and Name Sale/Trade

## 1. What this is

A paid title is a transferable claim that a specific person controls a `jttp://` name. It is
separate from the free `_kickl` TXT proof: a name can be TXT-verified without being owned, and a
title can exist on a name whose TXT proof is absent. The UI shows two different badges and never
merges them.

```
verified name   = DNS/TXT proof that the owner controls the real-TLD domain
paid title      = secupgrade.com badge saying who currently holds the name
```

## 2. Keys

| Item | Where | Purpose |
|------|-------|---------|
| RSA-3072 private key | owner credential dongle, never uploaded | unwraps the AES token; signs title statements |
| AES-256 ownership token | RSA-OAEP-wrapped at rest on the owner dongle; AES-GCM-encrypted in transit | the secret that opens the owner area and proves possession |
| RSA public key + token commitment | public record at secupgrade.com | lets the registry verify without storing the token in clear |
| Owner id (`kb1…`) | derived from SHA-256(RSA SPKI) | stable public handle |
| Badge id (`kb1b…`) | derived from site, owner, seq, issue time | changes when the title moves |

Why not "AES with a public key": AES has no public key and cannot sign. RSA-OAEP-SHA256 wraps the
one-use AES-256-GCM transport key to the recipient dongle; AES-GCM encrypts the authorized token
payload. After RSA unwrap, the receiver opens that AES packet. RSA-PSS signs title statements.

The paired-dongle passphrase is entered on the trusted first device after its prompt. It unlocks the
owner credential there and is never typed into or transmitted to the connected computer. The
computer still receives the explicitly authorized decrypted payload in memory, so this prevents
target-computer keylogging of the passphrase but cannot protect a compromised receiving endpoint
from capturing the payload.

## 3. Paired-dongle ceremony

```
1. The first Kickl device pairs and labels the owner and recipient credential dongles.
2. It creates the RSA-3072 owner keypair and 32-byte AES title token; the owner private key is
   encrypted at rest under a passphrase-derived key on the owner dongle.
3. The public record `{site, RSA SPKI, SHA-256(token commitment), ownerId}` is uploaded.
4. The registry sends a 32-byte challenge to the connected computer.
5. The first device prompts for the passphrase; the user enters it there to unlock the owner
   credential. The passphrase stays on the first device.
6. The first device signs the challenge with RSA-PSS and sends the authorized token payload in an
   AES-256-GCM packet. RSA-OAEP wraps the one-use AES key to the recipient dongle.
7. The recipient unwraps the AES key and opens the packet; the registry checks commitment and
   signature, then issues title `seq=1` and a badge.
8. Both dongles discard the one-use transfer key; the title remains until expiry, transfer, or closure.
```

The software slice now stores the owner secret in an Argon2id-derived AES-256-GCM envelope and
provides a paired-receiver packet API: RSA-OAEP-SHA256 wraps a one-use AES-256-GCM key, with site
and recipient identity authenticated as packet metadata. The emulator's `OWNER PAIR` action prompts
on the trusted-device screen, then exchanges a bounded `KDL1` protocol frame over loopback TCP with
a separate receiver process through the `IDongleTransport` adapter boundary. Its stable RSA identity
is encrypted at rest under the passphrase and checked against a
separate pinned public identity file. The private identity is sent to the child through an inherited
local stdin pipe to unlock the receiver process; this is a development simulator, not a hardened
cross-device channel. The CLI also asks for the owner-key passphrase without echoing it. Legacy
plaintext owner-key files are not silently unlocked by this version and need a deliberate migration
path. A configurable USB/HID transport now discovers only explicitly supplied VID/PID and optional
serial values, fragments KDL1 frames into KHF1 HID reports, and relies on RSA-PSS signed receiver
acknowledgments. The local UI verifies a signed challenge hello and requires fingerprint confirmation
before first pinning. The emulator UI has not been wired to physical USB yet; hardware IDs and dongle
firmware remain undefined. Physical Wi-Fi/Bluetooth discovery and
transport remain unimplemented. `IssuerSig` remains
`dev-unsigned`; paid tier is not implemented.

## 4. Badge lifetime

- **Active** for 12 months from issue; renewal re-runs the challenge.
- **Grace** 30 days after expiry; the badge shows "lapsed" and the owner area stays readable.
- **Transferred** when a buyer accepts a seller statement; the old badge is dead immediately.
- **Closed** when the owner closes the name; the badge is dead and `open` refuses the name.
- **Revoked** only for fraud/abuse, with a published reason and appeal path.

## 5. Owner account area

`jttp://secupgrade.com/owner` (and the local equivalent in the slice) requires paired-dongle approval:

- title status, sequence, expiry, renewal;
- **List for sale** with a price in BTC or NKL, or "free transfer";
- offers and buyer public keys;
- **Sell / Trade**: produce a transfer statement;
- close the name;
- audit log of title events (no browsing data, no device data).

The token unwraps the account vault; the RSA signature authenticates writes. The recipient computer
receives the payload only after the first device authorizes the transfer.

## 6. Transfer flow

```
seller: sign {site, fromOwner, toOwner, toRsaSpki, toTokenCommitment, seq+1, price, time}
buyer:  import statement, prove possession of the new key (challenge + token)
registry: verify seller signature against the current title, advance seq, new badge
```

Rules:

- `seq` must be exactly `current.seq + 1`; stale statements are rejected.
- The registry record must still match the seller's title (`title_seq`, `badge_id`); a stale local
  copy cannot sign a sale.
- The buyer must prove possession before the registry moves the title.
- After the move, the seller's key fails to sign. The old badge is invalid.
- Payment is between the parties (or escrow); the registry records `price` and `payment_ref` but
  does not custody funds in this phase.

## 7. Names without a title

- **Create**: publish a document; the name becomes `active` under the creator.
- **Close**: the creator marks it closed; `open` refuses it until republished.
- **No sale, no trade, no badge.** The UI says "unowned name; created and closed by its user".
- Claiming an unowned name is first-come, first-served, with rate limits and abuse review.

## 8. Storage and API

Client:

```
profiles/local/shared/ownership/<site>.pub.json      public record
profiles/local/shared/ownership/<site>.title.json    title + seq + badge
<owner dongle>/kickl-owner-<site>.key                passphrase-encrypted RSA private key + wrapped AES token
<recipient dongle>                                   recipient RSA key for the wrapped AES packet
<owner dongle>/kickl-owner-<site>.pub.json           copy of the public record
```

Server (see `reference/api.md`):

```
POST /ownership/register   {site, rsa_spki, token_commitment, owner_id}
POST /ownership/challenge  {site} -> {challenge, expires}
POST /ownership/verify     {site, challenge, token, rsa_signature} -> title
POST /ownership/list       {site, price, currency}
POST /ownership/transfer   {statement} -> new title
POST /ownership/close      {site, signature}
GET  /ownership/<site>     -> public title
```

Schema: `titles(site, owner_id, rsa_spki, token_commitment, badge_id, seq, status, tier,
payment_ref, issued_at, expires_at, issuer_sig)`, `title_events(id, site, seq, action, actor,
created_at)`. Never store the token in clear; the commitment is one-way.

## 9. Threat model

| Threat | Result |
|--------|--------|
| Someone copies the public record | cannot prove possession; no badge |
| Someone steals the owner dongle | passphrase-protected RSA private key; the paired recipient must also approve |
| Target computer keylogger | cannot capture a passphrase entered only on the trusted first device |
| Malware on receiving computer | may capture the payload after authorized decryption in memory |
| Registry database leak | token commitment is one-way; RSA private keys never leave USBs |
| Registry sees the token during verification | yes, in this design; use a derived proof in a later revision |
| Seller sells twice | seq + registry check rejects the second statement |
| Buyer refuses to pay after transfer | escrow or atomic swap; out of scope for the slice |
| Forged badge | only the registry can issue; client verifies `issuer_sig` (not implemented yet) |
| Phishing owner area | the area is only reachable through the pinned secupgrade.com identity |

## 10. Slice status

Implemented and tested:

- RSA-3072 key creation, AES token wrapped with RSA-OAEP in the current local slice;
- challenge → RSA-PSS signature + token commitment verification;
- title issuance, badge, registry link;
- seller-signed transfer, buyer proof, seq advance, stale-signature rejection;
- close/reopen semantics in the registry.

Not implemented:

- real payment (BTC/NKL), escrow, or a paid tier; the slice says "simulated";
- registry issuer signature;
- recovery if the USB is lost;
- encrypted owner-key storage, paired-dongle link, and the trusted-device passphrase prompt;
- token-derived proof that keeps the raw token off the server;
- a network owner area (local file stand-in).
