# 19 — Kickl handheld: two screens, physical keys, mesh

> Status: **specification**, not a built device. The desktop emulator now keeps the publications
> feed on its front screen using the supplied local XML file; the physical device and dongle pair
> are not implemented.

## 1. Shape

A clamshell handheld, closer to a DS than a phone:

```
        ┌──────────────────────────────────────────┐
        │  TOP SCREEN — touch, always available     │
        │  publications RSS scroller + stylus search│
        ├──────────────────────────────────────────┤
        │  hinge                                    │
        ├──────────────────────────────────────────┤
        │  INNER SCREEN — touch                     │
        │  browser · social · Nickles ad slot       │
        │                                          │
        │  game controls above detachable keyboard   │
        └──────────────────────────────────────────┘
       [USB 1] [USB 2] [USB 3] [USB 4] [miniSD] [miniSD]
```

- **Top/front screen:** 5.5–6", 1440×720, touch, matte. The public publications RSS remains visible
  as the front page while the device is locked. A stylus tap at the top edge
  pops a mini search field over the feed. This screen has no session keys and no page renderer.
- **Inner screen:** 7–8", 1920×1200, touch. The browser, chat, mail, and the full-width Nickles
  footer bar above the status edge.
- **Keyboard and controls:** detachable keyboard with game controls immediately above it; no built-in
  microphone.
- **Ports and cards:** four USB openings and two mini-SD card holders. The paired authentication
  dongles communicate with each other over Wi-Fi; USB is not their data transport.
- **OS:** MicroCore, a minimal operating system built specifically to host the Kickl system, with a
  signed immutable base and verified updates.
- **No camera, no microphone.** If a future revision adds either, it gets a hardware shutter and a
  distinct LED.

## 2. Top screen: RSS + stylus search

The top screen is deliberately dumb. It is a public news surface, not a second browser. The RSS feed
is the default front page and stays visible while the inner browser is locked or in use.

- Continuous vertical RSS ticker; tap pauses, tap resumes.
- A slim search field hides at the top edge; stylus tap or short downward drag reveals it.
- Characters are sealed per keystroke by `KeystrokeShield` under the live session key; no pointer
  path, hover, or dwell telemetry.
- Submit runs the PFS live-index query from `docs/21`. Hits appear as a compact strip on this screen.
- Selecting a hit does **not** open it. The device shows the confirmation sheet and hands off to the
  inner screen.
- Locked device: RSS still scrolls (it is public); search results require an unlock.
- Never shown here: blobs, mail, chat, ownership token, ads, or decrypted regions.

Why a separate screen: glancing at headlines should not expose the browser session, and a public feed
should not be able to paint over the private surface.

## 3. Compute and storage

| Part | Choice | Reason |
|---|---|---|
| SoC | ARM64, 8 cores, AES/SHA extensions, no Intel ME-style engine | one architecture, hardware AES |
| RAM | 8 GB | browser + one sealed page + cache |
| Storage | 128 GB NVMe, full-disk encryption, separate vault partition | profile isolation |
| Secure element | TPM 2.0 or discrete secure element | device key, boot measurement |
| Radios | Wi-Fi 6E, Bluetooth 5.3; optional USB-C Ethernet | no cellular by default |
| Battery | 6,000 mAh, USB-C PD, ~10 h mixed | fanless |
| OS | MicroCore, minimal Kickl host, signed read-only base and verified updates | small audited host surface |

No app store. Only signed Kickl plugins with declared capabilities run; everything else is a web
surface inside the browser sandbox.

## 4. USB ports, mini-SD holders, and paired dongles

The enclosure has four USB ports and two mini-SD card holders. The security ceremony uses a paired
set of credential dongles. The first device enrolls and labels the credentials; the companion dongle
is attached to the computer being connected to. The two dongles communicate over Wi-Fi. USB HID and
Bluetooth are not the selected dongle transport; USB may provide power or physical attachment, but
the software must not require a vendor-specific USB driver.

The user enters the credential passphrase on the trusted first device after its prompt. The
passphrase is not typed into or sent to the connected computer. The first device unlocks the
credential locally, then creates a one-use AES-256-GCM packet. RSA-OAEP-SHA256 wraps the one-use AES
key to the recipient dongle's RSA public key; the encrypted packet carries the authorized key/token
payload. The recipient unwraps the AES key with its RSA private key and opens the packet. The
passphrase itself never travels in that packet.

The two mini-SD holders are removable storage slots. Mounting and writing require explicit user
approval; they are not silently treated as credential dongles.

Physical-key rules:

- Unlocking a credential requires its passphrase on the trusted first device.
- Ownership proof requires approval on the first device and the paired recipient dongle.
- Disconnecting either dongle ends the transfer and discards its one-use AES key.
- A target-computer keylogger cannot record a passphrase that was never entered on that computer.
- After authorized decryption, malware on the receiving computer could still capture the plaintext
  payload in memory; the transport does not protect data from a compromised endpoint.
- Unknown USB devices and mini-SD cards are not auto-mounted.

## 5. Mesh

Each device has a stable RSA-3072 authentication key, pinned out of band by QR or USB. The key does
**not** encrypt traffic and is not rotated per opening.

A link is:

1. Initiator creates a fresh P-256 ECDH pair and a 16-byte nonce.
2. Offer: device ids, initiator ephemeral public key, nonce, RSA-PSS signature.
3. Responder verifies the pinned key and signature, creates its own ephemeral pair, signs the accept
   (both ephemeral keys + nonce), and derives the keys.
4. Initiator verifies the accept, derives the same keys, and discards its ephemeral private key.
5. Frames are AES-256-GCM with separate keys per direction and strict counters.

Recorded traffic cannot be decrypted later with the long-term RSA keys. That is the point of the
ephemeral exchange. Relays see ciphertext, device ids, and frame sizes only.

Abuse controls: per-peer rate limits, hop limits, no general internet exit, and store-and-forward
quotas. A mesh relay must not become a free anonymity network.

## 6. Bootstrap and unknown names

- Every unit ships with the pinned SPKI hash of `secupgrade.com`.
- Wi-Fi is configured on first run; the device never asks for a social account.
- Unknown name: `jttp://new.custom` returns "not found" over PFS. The device offers **Claim and
  build**.
- The site IDE opens locally with a starter KML document: title, headline, required tags, one page,
  preview. Publishing signs the record and takes the name.
- `secupgrade.com/mesh/` is the rendezvous/lookup point for peers and unknown names; it returns
  signed registry records, never page keys.
- The live index at `secupgrade.com/legenddxI2` supplies the title/headline/timestamp/domain list
  described in `docs/21`.

## 7. Power, thermals, repair

- Fanless; the SoC is capped for sustained browser work, not gaming.
- Charge limit 80% by default; battery is a replaceable part, not glued.
- Screws and a published teardown; no soldered RAM.
- Buttons: power, radio kill, panic (recessed, two-step hold).

## 8. Threat model

| Threat | Mitigation | Residual |
|---|---|---|
| Stolen device | encrypted profile, paired dongle approval, full-disk encryption, panic wipe | coercion and compromised host |
| Malicious removable device | no auto-mount, explicit approval, paired-key checks | firmware attacks on peripherals |
| Wi-Fi MITM | ephemeral ECDH + pinned RSA signatures | RF jamming, traffic analysis |
| Mesh abuse | quotas, hop limit, no exit | sybil peers |
| Supply chain | verified boot, signed updates, reproducible firmware builds | fab-level implants |
| Screen capture | top screen has no secrets; inner screen hardening is OS-dependent | camera pointed at panel |
| Evil maid | verified boot + physical tamper sticker | determined attacker |

## 9. Dongle transport contract

The software boundary is `IDongleTransport.ExchangeAsync(DongleFrame, CancellationToken)` in
`Kickl.Core.Ownership`. The current adapter launches a separate local receiver process over loopback
TCP. All adapters carry the same bounded frame format: `KDL1`, protocol version, message type, a
big-endian payload length (maximum 128 KiB), then the payload. The payload remains the RSA-wrapped
AES-256-GCM proof packet; a bearer adapter must not decrypt or log it.

The repository contains an experimental `UsbHidDongleTransport` with a `KHF1` HID fragment format,
but this is not the selected product transport and does not establish the driverless Wi-Fi design.
The device pairing dialog now has Wi-Fi discovery and candidate selection and can send hello/proof
frames through the `WifiDongleTransport`. Discovery uses `KDW1` multicast probes and bounded replies;
the selected endpoint then carries the existing length-limited `KDL1` frames over TCP. The adapter
has connection timeouts and cancellation. No physical receiver service or firmware has been
implemented, so this path has not been validated with actual dongles.

Wi-Fi discovery results must be treated as untrusted hints. A discovered dongle is not paired until
it answers a fresh `ReceiverHelloRequest` challenge with a valid signature, its key fingerprint is
shown for explicit confirmation, and that public key is saved. Every later connection must match the
saved identity; a changed key is rejected. The encrypted proof packet and signed challenge-bound
acknowledgment remain end-to-end authenticated by the dongle keys. Discovery must not transmit the
owner passphrase or any credential payload.

Both Wi-Fi topologies are required:

1. **Direct link:** one dongle hosts a private Wi-Fi network and the companion joins it. Use a
   random per-pair network credential; do not use a factory-wide password. Disable internet routing
   and client-to-client access beyond the paired endpoint.
2. **Existing LAN:** both dongles join a user-selected Wi-Fi network. Discovery uses a bounded local
   UDP probe/response and is limited to the active interface/subnet; it never scans the internet.

Both topologies connect to the same TCP byte-stream endpoint and carry identical `KDL1` frames.
Discovery advertises only protocol version, ephemeral endpoint/port, and a random discovery nonce;
it carries no identity secret, owner passphrase, or credential payload. Treat every advertisement
and discovered address as untrusted. The client then sends a fresh 256-bit `ReceiverHelloRequest`
challenge over the stream and verifies the dongle's signed identity before showing its fingerprint
for explicit first-pair confirmation. Existing pins are mandatory on later connections. A mismatch
fails closed and requires an explicit forget-and-repair action.

Give discovery and TCP connection attempts short fixed timeouts, cancellation support, bounded
datagrams/frames, per-peer rate limits, and clear disconnect cleanup. Each ownership request has a
fresh challenge; the receiver must reject a repeated or expired challenge and sign the acknowledgment
over receiver ID, site, challenge, and result. Drop the one-use AES key and partial frame on timeout,
cancel, or disconnect. Wi-Fi link security is defense in depth; application-level challenge
signatures and pinned identity remain required on both topologies. The local simulator's process
pipe and loopback link are development-only; they do not establish a physical-device security
boundary.

First-use discovery sends a fresh 256-bit challenge in a `ReceiverHelloRequest`. The dongle returns
its receiver ID and RSA public key signed with its private key. Kickl verifies the challenge and
signature, shows the SHA-256 public-key fingerprint for trusted out-of-band confirmation, and saves
the pin against the selected transport identity. Later connections must match that pin. A changed
key is rejected until the user explicitly forgets the prior pin and pairs again. The receiver signs
each proof acknowledgment over receiver ID, site, challenge, and acceptance result.

**Mesh and registry are separate services.** The paired Wi-Fi dongle provides the authenticated
peer link used by the mesh. It does not host the site registry or become a general internet gateway.
`secupgrade.com` hosts the signed update database for registered sites, including their current
metadata and versions. Clients verify signed snapshots and update proofs before applying them. The
mesh may relay eligible peer traffic or cached site packages, but it cannot authoritatively change
registry state. Site payload delivery and the central registry database have separate trust and
availability paths.

## 10. Status and open questions

Implemented in the slice: ephemeral mesh link, PFS lookup/search, session identity rotation,
sealed regions, ownership token, and the emulator's always-visible publications screen.

Implemented in emulator only: local owner-credential enrollment and trusted-screen passphrase prompt,
plus the `IDongleTransport` frame contract, separate receiver process, loopback proof exchange,
persistent encrypted receiver identity, and pinned public identity check. The simulator passes the
unlocked receiver key through a local process pipe, so this is not a hardened cross-device link. The
desktop Wi-Fi discovery and pairing adapter is now connected to the device pairing dialog, but no
physical receiver firmware implements the protocol yet. Not implemented: physical device,
MicroCore OS, production Wi-Fi dongle firmware/pairing UX, physical USB/mini-SD policy, stylus
input, site IDE, mesh transport, quotas, `/mesh/` deployment, battery/thermal design, certification.

Open questions: Q-16 USB power/data behavior; Q-17 top-screen privacy mode; Q-18 RSS allowlist and
refresh cadence; Q-19 direct-link provisioning and existing-LAN onboarding UX; Q-20 battery/thermal targets; Q-21
manufacturing and regulatory path; Q-22 price tier; Q-23 keyboard attachment standard; Q-24 mini-SD
capacity and write policy.
