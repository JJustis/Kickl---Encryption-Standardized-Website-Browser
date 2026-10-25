# 06 — Shell, Visual Language, and Interaction

## 1. Design thesis

"Cyberdeck, not toy." Near-black glass, hairline structure, phosphor light used sparingly as
*information* (state), not decoration. Dense but calm. The page is the hero; chrome is a thin
instrument panel. The hacker aesthetic shows up in typography, micro-labels, scanline textures at
low opacity, and light that travels along edges when something happens — never in unreadable text or
constant motion.

Three themes ship in 1.0:

| Theme | Surface | Accent | Use |
|-------|---------|--------|-----|
| **Obsidian** (default) | `#080B0F` → `#0E131A` | phosphor `#5CFF9D`, cyan `#39D8FF` | daily |
| **Phosphor** | `#050805` | green `#39FF14`, amber `#FFB000` | full hacker mode |
| **Paper** | `#F4F5F7` | indigo `#4C6FFF` | daylight / accessibility |

All three are token sets; no component hard-codes a colour.

## 2. Tokens

```jsonc
{
  "color": {
    "bg.0": "#080B0F", "bg.1": "#0E131A", "bg.2": "#141B24",
    "line.1": "#1E2833", "line.2": "#2A3746",
    "fg.0": "#E8EEF6", "fg.1": "#A9B7C6", "fg.2": "#6C7C8E",
    "accent": "#5CFF9D", "accent.dim": "#2E8F5B",
    "info": "#39D8FF", "warn": "#FFB000", "danger": "#FF5470",
    "sealed": "#8B7BFF", "ok": "#5CFF9D"
  },
  "font": {
    "ui": "Segoe UI Variable Text, Inter, sans-serif",
    "mono": "Cascadia Code, Consolas, monospace",
    "size": { "xs": 10, "sm": 11.5, "md": 13, "lg": 15, "xl": 20 },
    "tracking": { "label": 0.08, "body": 0 }
  },
  "space": [0, 2, 4, 6, 8, 12, 16, 24, 32, 48],
  "radius": { "sm": 4, "md": 8, "lg": 12, "pill": 999 },
  "elevation": { "1": "0 1 2 rgba(0,0,0,.5)", "2": "0 8 24 rgba(0,0,0,.55)" },
  "motion": {
    "instant": 80, "fast": 140, "base": 220, "slow": 380,
    "ease.out": "cubic-bezier(.16,1,.3,1)",
    "ease.inOut": "cubic-bezier(.65,0,.35,1)",
    "spring": "cubic-bezier(.34,1.56,.64,1)"
  }
}
```

Motion is capped at 220 ms for state changes and 380 ms for entrances. Nothing loops except a 2.4 s
breathing glow on the panic control when armed (and that stops under reduced motion).

## 3. Layout — the 36 px rail

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ 34–40 px (DPI-scaled)                                                        │
│ ┌──────────┬──────────┬─┐ ┌───────────────────────────────┐ ┌─┬─┬─┬──┬────┐ │
│ │ ● Nyx    │ ○ Mail   │+│ │ ⌂  jttp://nyx.custom/research │ │⟳│🛡│⧉│⊗ │ ≡  │ │
│ └──────────┴──────────┴─┘ └───────────────────────────────┘ └─┴─┴─┴──┴────┘ │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│                            VIEWPORT (everything else)                        │
│                                                                              │
│                                                                              │
│                                                     ┌──────────────────────┐ │
│                                                     │ ◉ chat   ✉ mail      │ │
│                                                     └──────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Total chrome height: **36 px** at 100% DPI, 40 px at 150% (clamped 34–44). No second toolbar, no
  bookmark bar, no status bar (status is a 22 px overlay that appears on hover/load only).
- Tabs live **inside** the same row as the omnibox, left of it. Max tab width 180 px, min 44 px;
  below 44 px the strip becomes horizontally scrollable with a subtle fade mask. `+` is always the
  last element of the strip, not of the window.
- Omnibox flexes to fill; rail buttons are fixed 28 px squares with 6 px gaps.
- Window controls (min/max/close) are drawn into the same rail at the far right, 46 px each, using
  the native hit-test regions so Snap Layouts and Aero Snap work. `WM_NCHITTEST` returns
  `HTMINBUTTON`/`HTMAXBUTTON`/`HTCLOSE` (or `HTCAPTION` for empty rail space).
- Double-click empty rail = maximize; drag = move; right-click = system menu.
- Backdrop: `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, Mica/Acrylic)` on Win 11; a static
  noise + radial gradient fallback on Win 10. The viewport is opaque (WebView2 cannot be transparent
  reliably, and shouldn't be).

### 3.1 Tab states

| State | Visual |
|-------|--------|
| Active | raised `bg.2`, 1 px top accent line, title in `fg.0` |
| Inactive | transparent, title `fg.1`, hover `bg.1` |
| Loading | 2 px indeterminate bar along the tab's bottom edge, accent gradient |
| Sealed | small `⌁` glyph before the favicon slot (no favicon for sealed pages) |
| Clearnet | small globe glyph + amber dot when JS is enabled |
| Audio | speaker glyph, click to mute |
| Unread (dock) | not on tabs — dock badges only |
| Pinned | 32 px wide, icon only, no close button |
| Crashed | red dot + "reload" tooltip |
| Discarded | dimmed 60%, "sleeping" tooltip (memory saver) |

Interactions: drag to reorder, drag out to a new window (≥ 40 px vertical offset), `Ctrl+1..8` select,
`Ctrl+9` last, `Ctrl+Tab` cycle, middle-click close, `Ctrl+W` close, `Ctrl+Shift+T` reopen last
closed (max 25, stored encrypted). Tear-out uses a translucent drag image and a 120 ms snap.

## 4. Omnibox

- 30 px tall pill, `bg.1`, 1 px `line.1`; focus → 1 px accent + 2 px outer glow (8% alpha).
- Left chip = security state:
  - `⌁ sealed` (violet) — `jttp://` with a verified record
  - `⌁ sealed · pinned` (violet + dot) — TOFU pin present
  - `! sealed · changed` (amber) — key/endpoint changed
  - `⚠ open web` (amber) — clearnet about to open in the modal
  - `⛔ invalid` (red) — malformed / unregistered name
- Right side: reload/stop (morphs), shield (cookie/cert panel), copy-link, and a `⋯` overflow.
- Typing shows suggestions in a 420 px dropdown, 6 rows max, keyboard-first:
  1. exact URL if parseable, 2. local registry matches (name, owner short code, age badge),
  3. vault items, 4. contacts, 5. commands (`> settings`, `> panic`, `> register`), 6. history
  (local, encrypted, clearable, off in private mode).
- **No network suggestions, ever.** A test asserts zero packets while typing.
- Paste of a 41-char `kb1…` address offers "open contact card", not "navigate".
- URL is displayed decoded but IDN shows punycode on hover; long URLs truncate in the middle.

## 5. Animated controls ("fancy buttons")

A shared `LightButton` control with a small state machine. Four variants: `Ghost`, `Solid`,
`Outline`, `Danger`.

**Idle → hover** (140 ms): background lifts to `bg.2`, 1 px border brightens, a 1 px light sweep
travels left→right across the top edge (gradient `transparent → accent 40% → transparent`, 380 ms,
once), icon colour shifts to `accent`.

**Hover → press** (80 ms): scale 0.97, glow collapses, a radial ripple emanates from the pointer
(220 ms, `accent` at 12% alpha, clipped to the pill).

**Press → commit**: a 1 px accent underline draws in 180 ms; if the action is async, the icon is
replaced by a 14 px arc spinner and the label becomes a mono progress caption.

**Toggle on**: the border becomes a 1 px gradient (`accent → info`), a 6 px outer glow at 10% breathes
for 2.4 s then settles to 4%, and a tiny 3 px dot appears on the leading edge.

**Danger (panic)**: `danger` accent, no sweep; hover shows a 3 s hold ring that fills clockwise;
release early snaps back with a 120 ms spring. This is deliberately *not* a click.

**Disabled**: 40% opacity, no sweep, `cursor: not-allowed`, tooltip explains why.

Implementation notes: WPF `VisualStateManager` + `DoubleAnimation`/`ColorAnimation` on a
`DropShadowEffect` and a `LinearGradientBrush` offset; ripple is a `Path` with a `RadialGradientBrush`
animated via `CompositionTarget.Rendering` (or Win2D/WinUI Composition if we take ADR-0002). Budget:
no more than 3 concurrent glow effects; effects are disabled at `Battery Saver` and when the window is
occluded. GPU memory: glow bitmaps are cached per size bucket (28/32/40 px).

**Reduced motion**: all of the above collapse to opacity/colour changes at 80 ms. No sweep, no ripple,
no breathing.

## 6. The dock (chat + mail)

Bottom-right, 12 px inset, 44 px collapsed pill:

```
collapsed:   ┌──────────────────────┐
             │ ◉ 2   ✉ 7           │      (badges = unread)
             └──────────────────────┘

expanded:    ┌───────────────────────────────────────┐
             │ FreeChat                    —  ×      │
             │ ─────────────────────────────────────  │
             │  (conversation list / thread)          │
             │                                        │
             │  [ message input                  ➤ ]  │
             └───────────────────────────────────────┘
             width 380, height 520, resizable 320–720
```

- **Two lobes, one control:** the pill has a chat half and a mail half; clicking a half opens that
  panel. `Ctrl+Shift+C` / `Ctrl+Shift+M` open directly. `Esc` collapses.
- The panel is a separate top-level window (not a child) so it can extend past the browser window,
  stay on top optionally, and be moved to another monitor. It remembers position per monitor.
- Collapsed pill auto-hides after 8 s of no pointer proximity while a video/fullscreen page is active
  (opacity 12%, returns on mouse-near-right-edge). User can pin it.
- Panels are **non-modal** and never steal focus on a new message; they pulse the badge once.
- Unread badge uses a 200 ms pop (scale 1 → 1.18 → 1) and a single 400 ms glow, then rests.
- Notification toasts: 320×72, top-right of the *screen* (not the window), 5 s, click to open,
  swipe/`Esc` to dismiss, never contain message text by default (privacy: "New sealed message from
  Nyx").
- Drag a file onto the chat panel → attachment flow; over 25 MB → refuse with a clear reason.
- Visual: `bg.1`, 1 px `line.2`, 12 px radius, Mica where the OS supports it; header in mono 10 px
  uppercase with 0.08 tracking; messages as 12.5 px text with 4 px meta line; sealed attachments show
  a lock chip until clicked.

## 7. Clearnet lane: system browser by default, contained mini-browser by choice

`jttp://` is never used in place of HTTP. Any `http://` or `https://` destination leaves the sealed
world through one of two paths:

| Path | When | What |
|------|------|------|
| **System browser** (default) | normal clicks, bare real-TLD input, LegendDX open-web | hands the URL to the OS default browser; Kickl does not proxy, inspect, or modify it |
| **Contained mini-browser** | user picks "Open in Kickl", or sets it as default | isolated modal window, JS off, disposable profile, no cookies/history |

The first time a clearnet URL is opened, a native chooser appears: `Open in your browser` (primary) ·
`Open in Kickl (contained)` · `Remember my choice`. The choice is a setting, not a silent default.

```
┌──────────────────────────────────────────────────────────────┐
│ ⚠ OPEN WEB   example.com                    JS off  ⤢   ×   │  ← 40 px
├──────────────────────────────────────────────────────────────┤
│                                                              │
│   (WebView2 #B / WebKitGTK #B — separate disposable profile) │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ cookies blocked · certificate verified · no history saved    │  ← 24 px
└──────────────────────────────────────────────────────────────┘
```

- The contained window is a top-level window sized 70% of the parent, centered, 600 ms scale+fade.
- `⤢` expands to a full window; `Esc` closes and **destroys the profile** (cache, cookies, storage).
- JavaScript **off** by default. Enabling is per-site, requires a click on the `JS off` chip and a
  confirmation ("This site can run code on your computer"), shows an amber banner while on, and resets
  on close.
- No downloads auto-open; downloads go to a quarantine folder, are never executed, and are scanned by
  Defender if available. `file://` and `javascript:` are blocked.
- No autofill, no password manager, no extensions, no camera/mic/geo by default (permission prompts are
  native and default-deny).
- Links that navigate from clearnet into `jttp://` hand off to the sealed surface with a visible
  transition and a "you are leaving the open web" toast.
- The open web is marked in amber everywhere: tab glyph, omnibox chip, window border (1 px amber at
  20% opacity).
- Kickl never rewrites `http(s)://` into `jttp://`; a clearnet URL cannot silently become a sealed page.

### 7.1 Local profile sign-in

- The gate lists existing local profiles and accepts a separate user key for each one.
- `Create profile` takes a 1–32 character ASCII name and a confirmed user key of at least 8
  characters, then creates a profile-specific key and data
  directory, and signs in. Names are normalized to lowercase hyphenated directory ids.
- Locking returns to the gate; selecting another profile discards the current session keys and
  profile-specific in-memory feed and tab data before opening the selected profile.
- Profiles are local to this device. Recovery and cloud sync are not implemented.
- Profile names are visible in local directory names. The shared public registry remains shared.

**Current implementation status (2026-10-25).** Local profile selection, encrypted profile roots,
the locked sign-in gate, profile creation, basic tab open/close/reopen and keyboard navigation exist.
Registration returns to the locked gate and requires a separate sign-in. The omnibox now shows a
small local dropdown for parseable addresses and prefix matches from the signed-in profile's local
registry; it performs no network queries, Up/Down changes the highlighted result, and Enter accepts
it. Core navigation and tab controls expose accessible names and keyboard hints. Vault/contact/history
suggestions, settings, drag/reorder and tear-out tabs, screen-reader/200% zoom verification, and
Windows/Linux custom window behavior remain incomplete; this implementation status does not claim
the full shell specification above is finished.

## 8. Panic — precise interaction

| Gesture | Result | Confirmation |
|---------|--------|--------------|
| Click panic | **Lock**: keys evicted from memory, UI blurred, PIN to resume | none (instant) |
| Hold 3 s | Arm menu opens | hold ring completes |
| Menu → `Session` | Close tabs, clear RAM, lock | none |
| Menu → `Chat + Mail` | Delete those stores + keys, keep vault | type `WIPE` |
| Menu → `Vault` | Destroy vault keys, delete containers | type `WIPE` |
| Menu → `Everything` | Full crypto-erase + WebView2 profiles + logs | type `WIPE` + hold 3 s |
| `Ctrl+Shift+Delete` | Same menu | — |
| OS screen lock | Auto-lock if enabled (default on) | — |
| Idle 15 min (configurable) | Auto-lock | — |

Wipe progress is a terminal-style log (mono, one line per store: `keys … destroyed`,
`vault … 214 containers removed`, `profiles … 2 removed`). On completion the app restarts to first
run. The UI states plainly: *"Keys are destroyed first. Ciphertext may remain; it is unreadable."*
It also lists known copies it cannot reach (shadow copies, cloud backups, pagefile).

**Duress PIN (optional, off by default):** a second PIN that opens a decoy profile and optionally
triggers a wipe after a delay. This is documented as *dangerous and unverifiable*; it can increase
risk in a real coercion scenario. Advanced users only, with a warning page.

## 9. Vault UI

- Grid of 220×140 tiles, virtualised; tile = blurred thumbnail + name + `n opened / m blobs` + age.
- Hover: tile lifts 2 px, thumbnail unblurs slightly (still coarse), 120 ms.
- Open: full-window viewer, image centered on `bg.0`, blob overlay dots on the image where sealed
  regions are. Click a dot → PIN (if not cached) → region resolves with a 180 ms blur-in.
- Top bar: name, site, epoch, "refresh from owner", "export (encrypted)", "delete", `Esc`.
- Export produces a `.kickl.png` file and a separate key file (`<name>.kickl.key`) unless the user
  chooses a passphrase-wrapped single file. The UI always explains the two-file risk.

## 10. Settings (compact, 6 sections)

`Identity` · `Mail & Chat` · `Vault` · `Privacy` (poll interval, Tor, telemetry — all off) ·
`Appearance` (theme, motion, density, scanlines) · `Security` (auto-lock, wipe scopes, update channel,
danger zone).

- Two-column, 720×560 modal, left nav 180 px, search at top.
- Every toggle has a one-line consequence sentence underneath.
- Destructive settings are in `Security → Danger zone`, separated by a 1 px `danger` rule, and require
  re-auth.

## 11. Keyboard map (defaults)

| Keys | Action |
|------|--------|
| `Ctrl+L` / `Alt+D` | focus omnibox |
| `Ctrl+T` / `Ctrl+Shift+T` | new tab / reopen closed |
| `Ctrl+W` | close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | next / previous tab |
| `Ctrl+1..8`, `Ctrl+9` | tab N / last |
| `Ctrl+Shift+C` / `Ctrl+Shift+M` | chat / mail panel |
| `Ctrl+Shift+P` | command palette |
| `Ctrl+Shift+B` | shelf (bookmarks overlay) |
| `Ctrl+Shift+V` | vault |
| `Ctrl+Shift+Delete` | panic menu |
| `Ctrl+Shift+K` | lock now |
| `Ctrl+K` | open search |
| `F6` | cycle focus (omnibox → viewport → news feed) |
| `↑` / `↓` while a news card is focused | move to the previous / next card |
| `Enter` / `Space` / `S` on a news card | open / save / save |
| `Alt+←/→` | back / forward |
| `F12` | devtools — **only** in debug builds or when `KML_DEV=1` |

## 12. Accessibility and quality bars

- Focus ring: 2 px `accent`, 2 px offset, never removed; `:focus-visible` semantics.
- All targets ≥ 28×28 px; rail buttons 28×28 with 6 px gaps (≥ 24 px effective).
- Contrast: body text ≥ 7:1 on Obsidian; `fg.2` only for non-essential meta (≥ 4.5:1).
- Reduced motion, high contrast, and 200% zoom tested in CI screenshots.
- Screen reader announces: tab changes, load state, blob unlock state, unread counts (polite live
  region), and panic countdown.
- No text baked into images; icons are vectors with accessible names.
- Localisation-ready: all strings via `.resx`, no concatenation, plural forms via ICU-style helper.
