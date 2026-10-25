# 13 — Home: LegendDX + Arcade

The new-tab surface is the front door of the product. It is **not** a blank page and **not** a
clearnet portal. It is a split workspace: the games array first, search second.

## Try it now: playable browser activities

Run from the repository root:

```powershell
dotnet run --project src/Kickl.Browser -- --demo --data .kickl-demo
```

The demo profile starts at a sign-in gate; enter the fixed sample key `1234`. It is only for local
experimentation, so do not put personal data in it. For a personal local profile, omit `--demo`; the
browser opens the create/sign-in gate instead.

- **Tic-Tac-Toe:** open the **ARCADE** home pane and select the native game. Play X against a small
  local CPU opponent; **PLAY AGAIN** resets the board.
- **Memory Match:** select **MEMORY** in the same pane. Flip cards to find eight pairs, watch the move
  count, and restart whenever you like. The board is native, offline, and script-free.
- **Drawn sealed pages:** open `jttp://svg-lab.custom` to see inline SVG rendered as native content;
  open `jttp://sealed-lab.custom` to try the sealed-page unlock interaction.
- **Publisher/ad demo:** open `jttp://ad-lab.custom` to see an opt-in site slot, or visit
  `jttp://secupgrade.com/adnetworking/publisher` to explore the local publisher flow. The creative
  catalog is local; it does not upload ads or record impressions/clicks.
- **Publications:** use the right-hand stream to browse the configured RSS/site cards. Opening a card
  requires an explicit confirmation.

The **SNAKE** and **2048** cards are still placeholders for the future sandbox runtime; they are not
playable yet. The phonebook, vault, mail/chat, and clearnet mini-browser are also not full products.

## 1. Layout

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 36 px rail                                                                   │
├───────────────────────────────┬──────────────────────────────────────────────┤
│                               │                                              │
│  ARCADE                       │  LEGENDDX                                    │
│  ┌────────────┐ ┌────────────┐│  ┌─────────────────────────┐                 │
│  │  game art  │ │  game art  ││  │ search the phonebook  ⌕ │                 │
│  │  Neon Drift│ │  Hex Cells ││  └─────────────────────────┘                 │
│  │  @nyx · 4k │ │  @ari · 9k ││                                              │
│  └────────────┘ └────────────┘│  recent · vault · contacts                   │
│                               │  ─────────────────────────                    │
│  ┌────────────┐               │  jttp://nyx.custom                            │
│  │  game art  │               │  jttp://notes.custom                          │
│  └────────────┘               │                                              │
│                               │  [ open the open web ]                       │
│  [ featured sealed site ]     │                                              │
│                               │                                              │
│                               │                                              │
└───────────────────────────────┴──────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────────────────┐
│ AD · NCKL · full-width home footer bar                                       │
└──────────────────────────────────────────────────────────────────────────────┘
```

- 50/50 split with a 1 px draggable divider; remembers ratio per window size. Below 900 px wide the
  panes stack (arcade first), and the arcade becomes a horizontal carousel.
- With no address open, the surface is this home. A new tab opens it; the home button returns to it.
- Both panes share the theme. No card shadows heavier than 1 px; art is the only saturated colour.
- The arcade pane has `this week / all time / new` tabs and a `surprise me` die.
- Both panes scroll; the home ad is a **full-width footer bar** below the browser pane and above
  the status bar, not an overlay and not a floating lower-left card. It is only present on the
  home surface; it collapses to a labelled strip instead of covering page content.

## 2. LegendDX pane

LegendDX is the existing search engine on secupgrade.com. Kickl does not clone it; it **federates**
with it under three modes:

| Mode | What happens | Privacy |
|------|--------------|---------|
| **Phonebook** (default) | local search over the registry mirror: names, handles, short codes, tags, descriptions | nothing leaves the device |
| **Vault & contacts** | local search of saved pages and contacts | nothing leaves |
| **Open web** (explicit) | opens LegendDX in the clearnet modal with the query | query + IP go to secupgrade.com; banner shown before sending |

Rules:
- Default is **Phonebook**. The open-web action is a separate button with the label "Search the open
  web with LegendDX" and a one-time consent dialog (remembered per profile).
- No search-as-you-type to the network. No query suggestions from the server.
- Results for phonebook: name, owner handle, age badge (`new` if < 7 days), sealed/open-web icon,
  description, and a `visit` action. Keyboard: ↑/↓ + Enter.
- Results for vault: thumbnail, title, `n/m blobs`, last opened.
- If LegendDX is unreachable, the open-web button is disabled with "engine offline" — local search
  keeps working.
- **API option (later):** a `GET /v1/legenddx?q=` proxy on secupgrade.com with k-anonymity batching
  (client sends a batch of decoy queries; server returns results for all; client picks its own).
  Only if Q-05 is approved. Until then, no API.

## 3. Arcade pane

### 3.1 What a game is

A game is a **KML document** with a `game` block:

```xml
<kml v="1" site="nyx.custom" epoch="2026-10-24T00:00:00Z">
  <head><title>Neon Drift</title></head>
  <body>
    <game id="g1" ref="blake3:9f2c…" runtime="wasm-1" w="960" h="540"
          title="Neon Drift" author="kb1q9x7…" entry="game.wasm" />
  </body>
</kml>
```

- `runtime="wasm-1"` = WebAssembly + a tiny, fixed JS shim inside a sandboxed web view.
- The bundle is a content-addressed blob (`.kickl-game` zip) containing `game.wasm`, assets, and a
  `manifest.json`. No native code, no network, no filesystem.
- The game view runs in a **separate process** (WebView2 with a disposable profile / WebKitGTK with a
  sandboxed web context), with:
  - no network (`connect-src 'none'`, request filter blocks everything),
  - no clipboard, camera, mic, geolocation, notifications,
  - no access to the Kickl bridge except a narrow `arcade` API,
  - a 256 MB memory cap and a 5-minute CPU watchdog (pause menu, not kill),
  - `requestAnimationFrame` only; no `eval` of remote code (the bundle is signed and local).

### 3.2 The arcade API (exposed to the game sandbox)

```ts
arcade.ready(): void
arcade.score(n: number): void          // integer, monotonic per run
arcade.gameOver(stats): void
arcade.storage.get(key): string|null   // namespaced per game, 64 KB
arcade.storage.set(key, value): void
arcade.profile.handle(): string        // display handle only, no address
arcade.exit(): void
```

No network, no timers beyond the frame loop, no `fetch`, no `WebSocket`, no `postMessage` to the
parent except the above. Cheating is possible; the leaderboard is **social**, not an authority.

### 3.3 Fame mechanics (the point)

- **Plays** (started runs), **finishes**, **likes** (one per identity per game, signed).
- **Streak** and **weekly board** (resets Monday 00:00 UTC); all-time board separate.
- **Remix lineage**: a game can declare `forkedFrom`; the lineage tree is shown on the game page.
- **Profile page** (`jttp://profile.kb1…`? no — resolve by address in the app, not a domain): handle,
  games, plays, likes, featured badges, joined date.
- **Instant fame**: the home feed ranks by `score = plays^0.6 + likes*3 + finishRate*10 + recency`,
  with a hard cap of one slot per author per day so one person cannot own the front page.
- **No money** attached to plays or likes in 1.0. No tokens for engagement (avoids gambling/security
  issues).
- Anti-gaming: one play per identity per 10 minutes per game, IP-independent; likes require a 24 h
  old identity; the board shows "unverified" if anomaly detection triggers.

### 3.4 Publishing a game

`Arcade → Publish` → builder (or upload a `.kickl-game`) → validation:
- bundle ≤ 20 MB, wasm ≤ 8 MB, assets ≤ 12 MB,
- manifest schema valid, entry point exists, no network imports (`wasi_snapshot_preview1` allowlist),
- security scan (wasm imports, strings, zip-bomb ratio),
- human review for the `featured` slot only (publishing is instant; featuring is curated),
- signature by the author; catalog countersigns.

Moderation: report button, takedown policy (malware, hate, sexual content involving minors, gambling),
and a public log of removals. Games are public by design.

## 4. Home state and privacy

- The home page stores: last search (local), recently visited (encrypted), arcade filter, split ratio.
- No analytics, no "trending from the server" unless the client explicitly fetches the (public, signed)
  arcade feed. The feed carries no user identifiers.
- The ad slot is the only commercial element and is governed by `docs/15`.

## 5. Failure and empty states

| State | UI |
|-------|-----|
| Registry not yet synced | "Phonebook is downloading (2.1 MB)…" with progress; search disabled |
| No games | curated starter pack shipped with the app (3 small CC0 games) |
| Game crashes | card shows "crashed" + `report` + `try again`; never takes the browser down |
| Offline | home still renders; arcade shows cached games; LegendDX open-web disabled |
| Ad slot empty | collapses to a 1 px `AD` strip; no house ads pretending to be content |
