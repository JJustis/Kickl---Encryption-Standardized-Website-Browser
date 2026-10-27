# 28 — Sharing games between computers

> Scope: the slice as implemented (`Kickl.Core/Games/GameCard.cs`, `GameRegistryClient.cs`, `GameSeedServer.cs`, `GameDownloader.cs`, `GameSharing.cs`,
> `server/src/GameCardStore.php`). A game published on one computer can be downloaded, from the arcade, by every other computer running Kickl, and each computer
> that downloads it becomes another place to get it.

## 1. The idea

The central registry (Pinwheel, `secupgrade.com/apicalls`) stores **cards**, not games. A card is a small signed record: the game's title, tagline, author and
colour, the **SHA-256 and size of its file**, a version, and the publisher's public key. The registry also keeps a list of **sources**: computers that currently hold
a game and will hand it over. The game file itself never goes through the registry; it travels directly between computers.

```
   publisher's computer                registry (Pinwheel)                 another computer
   ───────────────────                 ───────────────────                 ────────────────
   games/my-game/game.wasm                                                  arcade shows a dimmed card
        │  sign card (ML-DSA-65)                                            "DOWNLOAD · 15 KB · 2 sources"
        ├──────── POST card ─────────►  stores the card  ◄───── GET cards ──┤
        ├──────── "I can serve sha" ─►  stores the source                    │  click → "Download this game?" → DOWNLOAD
        │                                                                    ├── GET sources ─────►  [list of computers]
        │◄════════ TLS 1.3, pinned: GET /v1/game/<sha256> ═══════════════════┤
        │          (or from any other computer that downloaded it)           │  check: signature, size, SHA-256, sandbox rules
        │                                                                    │  install in the games folder, play it
        │                                                                    └─ "I can serve sha too" ─► registry (another source)
```

Nothing is downloaded until the reader clicks a card and confirms. Games spread by being clicked: a game that nobody has clicked is only on its publisher's
computer.

## 2. The card

```json
{ "v": 1, "id": "crystal-caverns", "title": "Crystal Caverns", "tagline": "…", "author": "kickl", "accent": "#7be0ff",
  "sha256": "…64 hex…", "size": 15597, "version": 1,
  "publisherKey": "…ML-DSA-65 public key, base64url…", "publisherId": "1a1274cc…", "created": 1792800000, "sig": "…" }
```

- **`sig`** is an ML-DSA-65 signature over every field (each length-prefixed, so field boundaries cannot be shifted to reuse a signature).
- **`publisherId`** is the first 16 hex characters of the SHA-256 of `publisherKey`. A publisher id cannot be claimed without the key.
- A card is identified by `(publisherId, id)`. Two publishers can both have a game called `plasma`; they are two different cards, and a downloaded game from a different
  publisher gets its own folder (`plasma-1232c72f`) and never overwrites yours.
- Every client **verifies every card** it receives (shape, key hash, signature). A card that fails is dropped and never shown. The registry cannot forge or alter
  a card, because it does not hold the publisher's key, and cannot make a client accept a file that is not the one the card names.
- The registry sets one unsigned field, `sources`, the number of computers listed as having the game, shown on the card.

## 3. Publishing a game

Put the game in your **own games folder** (`<your data folder>/games/<id>/game.wasm`, see `games/README.md`) and press **SHARE THIS GAME** on its arcade card, or:

```
kickl games publish my-game --pin YOUR-PIN
```

- The browser makes a **publisher key** for your profile the first time (an ML-DSA-65 key, kept encrypted in the profile) and a secret **token**. The registry stores only the
  token's hash, so only you can update or remove your cards (the same pattern as mail identities).
- Publishing again after the file changed raises the card's `version`; publishing an unchanged file does nothing new. A game's `game.json` keeps the signed card beside it.
- Only games in your own games folder offer SHARE; the games that ship with the program are already everyone's.
- A publisher may have at most 30 cards. Cards are limited to 2 MiB per game (the sandbox allows 512 KiB on an unclaimed name; a larger game is listed but the
  sandbox refuses it where the limit is lower).
- To remove a card: `POST /v1/games/remove` with the token (the operator can also remove any card with the admin token).

## 4. Downloading

Cards the registry knows about appear in the arcade after the local games, dimmed, as **DOWNLOAD** (or **UPDATE**, when you hold an older version from the same
publisher). Clicking one opens a prompt that shows the title, author, **publisher key fingerprint**, size, version and how many computers can send it. If you accept:

1. The browser asks the registry for the game's **sources** and tries them one at a time (at most 8). Each source is contacted over **TLS 1.3, pinned to the key in its
   address** (`https://host:port#pin=…`, the same scheme as direct mail). Every address has a second form, the address the registry saw the computer come from, tried next.
2. Whatever a computer sends is accepted **only if it is exactly the file the signed card names**: the right length and the right SHA-256. A computer that is offline, slow, lying,
   or pointing somewhere else is skipped; one honest source is enough.
3. The module must load in the game sandbox (no imports, valid WebAssembly) and fit the limits. Only then is anything written, into your games folder.
4. The game opens. Your computer announces itself as another source.

If no computer is online that has it, the prompt says so and you can try again later.

## 5. Serving what you have

While the profile is unlocked and **SHARING: ON** (the default; one click turns it off), the browser runs a small server (`GameSeedServer`) and keeps the registry told
that it can serve the games it holds (re-announced when it starts; a source that has not re-announced for 36 hours is forgotten).

- It answers **one** request, `GET /v1/game/<sha256>`, with that game's file or a 404, and only for games in your games folders.
- It is built for hostile peers like the mail listener: TLS 1.3 only, at most 12 simultaneous connections (the rest are dropped), a hard 30-second deadline per connection, an 8 KiB
  request limit, nothing else served. A computer that sends garbage is dropped without a reply.
- Its key and port are made once and kept (encrypted) in your profile, so your address stays the same between runs.
- It can be reached on your local network without any setup. To be reachable from the internet the port must be forwarded on your router, as for mail; otherwise computers
  outside your network simply find you offline and use another source.

## 6. What this protects, and what it does not

| Threat | Result |
|---|---|
| The registry (or someone who breaks into it) changes a card | the signature fails on every client; the card is dropped |
| A computer sends a different game, or a damaged one | rejected: the bytes must match the card's SHA-256 and size |
| Someone sits between you and a source | rejected: the connection is pinned to the key in the source's address |
| A malicious game | runs in the sandbox with no imports (no network, files, clock), a memory cap and an instruction budget; it cannot reach anything. It can still draw misleading text |
| Someone publishes a game as someone else (same title) | **not prevented.** Cards show the **publisher key fingerprint**; a title and author are only what the publisher typed. Compare fingerprints for a game that matters |
| Someone floods the registry with cards or sources | limited: 12 publishes and 120 announcements an hour per address, 30 cards per publisher, 25 sources per game, 5,000 cards in all. The operator can remove any card |
| A source points the downloader at an arbitrary address | the downloader contacts at most 8 addresses, over a pinned connection that sends one fixed request and never follows redirects, then stops; the content is checked regardless |
| The registry learns who downloads what | it sees the requests for sources and cards from your address (as it sees every request), and learns that a computer holds a game when that computer announces itself. Turn SHARING off to stop announcing |
| Nobody online has the game | it cannot be downloaded until its publisher or a mirror is online |

## 7. The registry API

All under `/v1/games`; every endpoint is rate limited per address. The tables are created by `server/migrations/003_game_cards.sql`
(`mysql -u root kickl_registry < server/migrations/003_game_cards.sql`).

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/games?since=N&limit=M` | cards changed at or after `N` (whole seconds, inclusive; a client merges duplicates), oldest first, up to 200; `next` and `more` page forward |
| `POST` | `/v1/games/card` | publish or update a card (`{card, tokenHash}`); a newer `version` and the same token are required to update |
| `POST` | `/v1/games/remove` | remove a card (`{publisherId, id, tokenHash}`, or the admin token) |
| `POST` | `/v1/games/source` | `{sha256, endpoint}`: this computer can serve that game. Refused for an unknown hash or an endpoint that is not `https://host:port#pin=…` |
| `GET` | `/v1/games/sources?sha256=H` | up to 12 fresh sources, each with its own address and, where it differs, the address the registry saw |

The registry cannot verify an ML-DSA signature (PHP has none), and does not need to. It enforces structure, size limits, that the publisher id is the hash of the key in the card,
and ownership by token; clients do the signature checking.

## 8. Commands

```
kickl games remote  --pin PIN [--registry URL]          list the cards the registry knows, and which you have
kickl games publish <id> --pin PIN [--serve]            share one of your games (--serve keeps serving it until Ctrl+C)
kickl games fetch   <id | publisher/id> --pin PIN       download a game you do not have
```

`--registry` points at another registry (for example a test one). Tests: `GameSharingTests.cs` (a real transfer between three profiles over TLS, liars, impostors,
forged cards) and the registry's PHP checks were run against a throwaway database with the real client.

## 9. Not done

- **No central copy.** If nobody who has a game is online, it cannot be downloaded. A registry or a volunteer could keep a copy; this slice does not.
- **No reputation, reports or review.** There is an admin removal and rate limits, nothing more.
- **Updates are shown but not pushed:** you see UPDATE on a card when the publisher has a newer version; nothing updates by itself.
- **Reachability from the internet** needs a forwarded port (or a relay); there is no NAT traversal.
