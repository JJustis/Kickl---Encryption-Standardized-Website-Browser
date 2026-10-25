# 09 — Nickles (Testnet)

## 1. Straight answer first

The brief says the browser "leverages a currency into existence". Software can create a **ledger** and
a **unit of account**; it cannot create *value*. Value comes from people accepting the unit, and the
moment a unit is redeemable, transferable at scale, or sold, you are in regulated territory
(money transmission, possibly securities, AML/KYC, tax reporting, sanctions screening).

Therefore:

- **Target for 1.0:** if implemented, Nickles is an explicitly labelled testnet with no cash value, no exchange, no fiat
  on/off ramp, no promise of future value, and a persistent banner in the wallet UI.
- This is a target design, not a claim about current code. The repository currently has no Nickles
  ledger, node, wallet, or explorer; see the implementation checklist at the end of this document.
- Nothing in the browser's core features depends on Nickles. It is an optional assembly that can be
  omitted from the installer (`Kickl.Nickles`), which also removes the legal surface.

**Do not** ship marketing language like "earn money", "investment", "price", "yield", or "leverage".

## 2. What "leverage into existence" becomes technically

Three legitimate readings, all implementable:

| Reading | Mechanism | Honest description |
|---------|-----------|--------------------|
| **Issuance** | A fixed genesis allocation + a published, auditable emission schedule | "New units are created by a rule everyone can verify." Not value. |
| **Credit** | Local points issued by a community/merchant, redeemable only for that merchant's goods | "Store credit", not currency |
| **Reward** | Units granted for useful work (relay uptime, seeding blobs, bug reports) | "Points for contribution" |

The proposed 1.0 design is **Issuance + Reward** on testnet. Credit is a governance decision for a
later version. These mechanisms are not implemented in the current repository.

## 3. Ledger design (testnet)

- **Model:** account-based, not UTXO (simpler for a browser wallet and for reading balances).
- **Block time:** 60 s target; PoW with a memory-hard-ish hash (SHA-256d for simplicity, difficulty 20
  at genesis, retarget every 144 blocks). It is a toy chain; do not pretend it is Bitcoin.
- **Consensus:** longest-chain with checkpoint signatures from the operator for fast sync. This is
  **not** decentralised consensus; call it what it is — a testnet with a checkpointing operator.
  2.0 would need a real design (federated BFT or a mature PoW with real hashpower).
- **Supply:** 21,000,000 NKL total, 8 decimals, genesis 10% to a published faucet address, 90%
  emitted over 20 years, halving every 2,100,000 blocks.
- **Fees:** 1 NKL flat testnet fee, burned (provably unspendable address).
- **Addresses:** reuse the `kb1…` identity encoding with a different HRP (`nkl1…`) so a wallet address
  is visually distinct from a mail address. Same keys? **No** — separate key derivation path
  (`m/44'/…'/0'/0/i`) so a mail key compromise does not drain a wallet.
- **Transactions:** signed with Ed25519 + ML-DSA-65 (same hybrid rule); nonce per account; memo ≤ 256
  bytes, encrypted if the sender chooses.
- **Replay:** chain id in the signature domain; testnet chain id is `kickl-testnet-1`.
- **Privacy:** transparent ledger (addresses + amounts public). Do **not** claim privacy; if privacy is
  wanted later, that is a different design (confidential transactions / shielded pool) with its own
  audit.

## 4. Wallet UX

- Settings → Nickles (off by default) → "Enable testnet" with a full-screen explanation and a
  checkbox that is **not** pre-checked: "I understand this has no monetary value."
- Wallet panel: balance, receive (address + QR), send (address, amount, fee, memo), history,
  "faucet" button (rate-limited, testnet only), and a chain-status chip (height, sync, peers).
- A permanent amber banner: `TESTNET · NO VALUE · NOT AN INVESTMENT`.
- Sending requires: valid checksum, sufficient balance, fee confirmation, and a 2 s hold on the send
  button (prevents fat-finger transfers).
- No fiat display. No price feed. No "portfolio".

## 5. Integration points (why it lives in a browser at all)

| Feature | Value | Risk control |
|---------|-------|--------------|
| Pay a relay for bandwidth (micro-fees) | aligns incentives for relay operators | off in 1.0; testnet only |
| Tip a sealed-page author | direct creator support | testnet only; no fiat |
| Pay for blob storage beyond quota | sustainable hosting | testnet only |
| Prove work / sybil resistance for registration | replaces PoW with stake | **not** in 1.0 (stake = money = regulation) |

The only 1.0 integration is a **read-only explorer panel** plus faucet transactions. Everything else
is deferred.

## 6. Legal and compliance gate (mandatory before any promotion)

- [ ] Counsel opinion: is NKL a security (Howey) in the target jurisdictions?
- [ ] Money transmission / MSB analysis (US state + FinCEN), e-money rules (EU), and licensing.
- [ ] AML/KYC program if redeemable, with a transactions-monitoring vendor and a SAR process.
- [ ] Sanctions screening (OFAC/EU/UN lists) for addresses and counterparties.
- [ ] Consumer protection: no misleading claims, clear risk disclosure, complaints process.
- [ ] Tax treatment documented for users (income at receipt? capital gains?).
- [ ] App-store and payment-processor policy review (crypto features can cause rejection).
- [ ] Crypto export/notification requirements (mass-market software is generally exempt, but check).
- [ ] Insurance and incident response for key theft / double-spend.
- [ ] Public postmortem and disclosure policy for chain incidents.

Until every box is ticked, the code path is **testnet-only and cannot be enabled by a build flag in
release**. A compile-time constant plus a signed remote config gate (both required) enforces this.

## 7. Prior art on secupgrade.com

The existing site already runs "Security Credits" (accounts, listings, token transfers) and a
"PythonCoin" ad slot. That is useful precedent for *credits as store value within a community*, not for
a general-purpose currency. If Nickles ever becomes real, the cleanest path is:

1. Keep it a **closed-loop credit** redeemable only for Kickl services (relay, storage, tips), which is
   usually outside money-transmission rules if genuinely closed-loop.
2. Publish reserve accounting if any fiat backing exists.
3. Never promise appreciation.

## 8. Engineering plan (if/when enabled)

| Step | Deliverable | Gate |
|------|-------------|------|
| N1 | Chain params, genesis, address encoding, tx format | spec review |
| N2 | Node: block validation, mempool, PoW, checkpoint sync | unit + adversarial tests |
| N3 | Wallet in browser: keys, signing, balance, history | UX review |
| N4 | Faucet + explorer (read-only) | abuse limits |
| N5 | Relay micro-payments simulation | load test |
| N6 | Security review + legal sign-off | **release gate** |

Estimated effort: 6–10 weeks for a credible testnet; 6+ months and a legal budget for anything real.

## 9. Explicit non-claims

- Nickles is not money, not an investment, not redeemable, not scarce in any economically meaningful
  sense, and not private.
- The testnet can be reset, forked, or wiped at any time without notice.
- Balances are not customer funds and are not insured.
- No roadmap promise of mainnet exists in this document.

## 10. Implementation checklist (2026-10-25)

- [x] Testnet-only limits and no-value disclosures are documented.
- [ ] Implement the testnet ledger, consensus/checkpoint verification, transaction validation, and
  explorer. The current repository has no wallet/node implementation.
- [ ] Implement wallet keys, receive/send flows, balance/history, faucet, and chain status.
- [ ] Implement the optional sandboxed Nickles plugin and capability broker described in `docs/14`.
- [ ] Complete legal, abuse, and security review before enabling any payment flow.

The browser currently has local publisher/ad-slot code; that is not a Nickles ledger or wallet.
