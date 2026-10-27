# 23 — The Kickl Certificate Authority

> Scope: the slice as implemented (`Kickl.Core/Trust`, `tools/Kickl.Ca`, `server/src/CaGateway.php`,
> the site in `htdocs/secca`). The CA watches ownership certificates; it does not decide who may use a name.

## 1. What it does

Buying a name at the BTC shop gives the buyer a **receipt**: the shop's signed statement that one name belongs
to whoever holds it. The CA countersigns receipts. A countersigned receipt carries a **seal**, and a site whose
seal verifies shows **✓ VERIFIED OWNER** to every visitor.

```
shop receipt (.kcert) ──►  CA  ──►  receipt + seal (.kcert)
                            │
                            ├─ checks: shop signature, not expired, not revoked, no clashing claim
                            ├─ appends a leaf to the append-only transparency log
                            └─ signs the seal and the log head
```

Nothing is manual. The CA applies the same rules to every receipt, and everything it has sealed is public.

## 2. The chain of trust

```
CA root (offline, hybrid Ed25519 + ML-DSA-65)    <- public key built into every browser
   └─ issuing key certificate (90 days)            <- signed by the root, offline
        ├─ seal                                    <- signed by the issuing key
        └─ transparency-log head                   <- signed by the issuing key
```

- Every CA signature is hybrid and **both halves must verify**. There is no classical-only mode.
- Each purpose has its own domain label (`ca-issuer-cert`, `ca-seal`, `ca-log-head`, `ca-revocations`), so a
  signature for one purpose cannot be replayed as another.
- A browser trusts only the root it was built with. Everything else is proven: root → issuer → seal → log.

## 3. What a seal proves

A seal names the site, the claim number, the hash of the receipt and a commitment to its secret token (never the
token), the receipt's expiry, and the seal's position in the log with an RFC 6962 inclusion proof. A visitor's
browser checks, in order: the issuer certificate is signed by the root, inside its dates and not revoked; the seal
is for this site and this claim number; it has not expired or been revoked; the issuer signed the seal and the log
head; and the seal is really in the log.

The seal is published **with the site** and is outside the document's integrity hash, so sealing never changes a
site's registry content hash.

## 4. What the CA watches for

| Situation | Result |
|---|---|
| Receipt not signed by a trusted shop | refused (`bad-receipt`) |
| Receipt expired | refused (`expired`) |
| Same receipt sent again | the same seal is returned |
| A different receipt for a claim number already sealed | refused (`conflict`), and an alert is written |
| A receipt older than the newest claim on the name | refused (`stale`) |
| Claim revoked | refused (`revoked`) |

`kickl-ca audit` re-derives every seal's inclusion and checks that the log only ever grows. The CA website runs it
(cached for a minute) and shows the result.

## 5. Revocation

A revocation list is monotonic: clients keep the highest number they have seen and ignore anything older.

- The **root** may revoke issuing keys and individual claims.
- An **issuer** may revoke individual claims only. A stolen issuing key cannot revoke a sibling issuer or undo a
  revocation. A list that claims `root` but is not signed by it is rejected.

Browsers refresh the list in the background at most every six hours and keep the old list if a fetch fails.

## 6. Running it

| Task | Command |
|---|---|
| Create the root (offline machine) | `kickl-ca root-keygen --dir ROOT` |
| Create an issuing key (online machine) | `kickl-ca issuer-keygen --dir server/keys/ca/issuer` |
| Certify it (offline, root present) | `kickl-ca issue-issuer --root-dir ROOT --issuer-public …/issuer-public.json --out server/keys/ca/issuer-cert.json --days 90` |
| Revoke claims (issuer) | `kickl-ca revoke --out revocations.json --previous revocations.json --seal site:seq:reason` plus the issuer options |
| Revoke an issuing key (root) | `kickl-ca revoke --root-dir ROOT --issuer-serial N --out revocations.json` |
| Check the log | `kickl-ca audit` plus the issuer options |

Files under `server/keys/ca/` (never committed): `issuer/`, `issuer-cert.json`, `root-public.json`,
`shop-keys.txt` (the shop public keys the CA will seal receipts from), `revocations.json`, `data/` (ledger).

**Renew the issuing certificate before it expires** (it lasts 90 days). Rotate the shop key by adding the new key to
`shop-keys.txt` first.

## 7. Before going live

The hierarchy created during development has its root on the same disk as everything else. For a real launch:
generate a new root on a clean offline machine, certify a new issuing key, replace
`src/Kickl.Core/Trust/ca-root.json` with the new root's public key, rebuild, and move the root private files
to offline storage.

## 8. One CA: secupgrade.com/secca

The browser talks to exactly one CA, `https://secupgrade.com/secca/`. In a shipped (Release) build that address is a constant:
plain http, any other host, any other port and any redirect are refused, a receipt is never sent anywhere else, and the CA
root is only the one built into the browser (there is no root file in the data folder to override it). A different CA would need
a different build.

## 9. Trying it without Bitcoin

`test-claims.bat` starts the browser against a throwaway test shop (`localhost/btcshop-test`, with a "mark as paid"
button) and a throwaway test CA (`localhost/btcshop-test/secca`, its own root and ledger). It only answers this
computer, uses a separate data folder, and shows **VERIFIED · TEST CA** so a test seal can never pass for a real
one. The real CA refuses test-shop receipts, and the browser trusts the test root only while the
`KICKL_TEST_CA_ROOT` variable the batch file sets is present, **and only in a Debug build**: Release browsers ignore both
test settings. Delete `htdocs/btcshop-test`,
`btcshop-test-private` and `%USERPROFILE%\kickl-test` when you are done.

## 10. Known limits

- A seal proves the CA saw a genuine receipt and logged it; it does not prove the buyer is a particular person.
- Browsers do not yet check the log's **consistency over time** themselves (the audit does); a malicious CA that
  rewrote its log would be caught by an auditor, not by a single visitor.
- The revocation list is fetched from the CA itself. A visitor who cannot reach it keeps the last list they saw.
- The issuing key is online; the 90-day certificate bounds the damage if it leaks.

## Owner binding (who may edit a sealed name)

The shop's `.kcert` is a bearer file: whoever holds it could edit the name. To tie a claim to a person, the owner may choose an
**owner passphrase** when the receipt is first sealed.

- The browser computes `HMAC-SHA256(Argon2id(passphrase, salt), "kickl/v1/owner-binding|site|seq|token-commitment")` and sends only
  the salt and the HMAC to the CA, in the receipt (they are not part of what the shop signed).
- The CA checks they are well-formed, puts them in the **seal** and so under its hybrid signature and in the transparency log. The CA
  never sees the passphrase. A seal made without them keeps exactly its old signed text.
- A name whose seal carries a binding opens for editing only when the passphrase is given (`UNLOCK WITH OWNER PASSPHRASE`, or the
  field on the `/edit` page). Importing the file on another profile leaves the name locked. The unlock lasts for the session.
- Resubmitting a receipt returns the existing seal, so someone holding a copy cannot seal it again without the binding.

Limits: the first seal decides. A claim sealed without a passphrase stays unbound, and a thief who seals a stolen file before the owner
does would bind it themselves. Seal with the passphrase right after the purchase. The CA's web page takes the passphrase too (it uses PBKDF2-SHA256 with 600,000 rounds through WebCrypto, labelled `pbkdf2-sha256-600k` in the seal; the browser uses Argon2id); both verify in the browser. A copy of the file with its `seal` field removed is unbound until the browser next reaches the CA, which
returns the bound seal.

### The shop binds it at purchase (closes "the first seal decides")

On the BTC shop page the buyer can type an owner passphrase before ordering. The page sends the shop only
`HMAC-SHA256(PBKDF2-SHA256(passphrase, salt, 600,000), "kickl/v1/owner-binding-shop|site")` and the shop **signs it into the
certificate** (`boundKdf`, `boundSalt`, `boundBinding`, appended to the signed text as `|bound:kdf:salt:binding`; a receipt
without one signs exactly the old text). Because it is inside the shop's signature:

- it cannot be stripped from the file or swapped for another (the signature stops verifying);
- the CA seals the **shop's** binding and ignores any binding a sealer adds, so sealing first gains nothing;
- the browser locks the name from the moment the file is imported, sealed or not, until the passphrase is given.

The earlier seal-time binding (browser or CA page) still exists for receipts the shop did not bind. The shop's `config.php`,
`api.php` and `index.php` live only on the server; they were patched in place (backups beside the user's profile folder).
