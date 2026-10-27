# 22 — WebAssembly games

> **New to this? Start with the walk-through:** [`docs/26-game-tutorial.md`](26-game-tutorial.md) builds four games from a gradient to a 3D maze, and
> `kickl game-check` (below) measures a game against the limits before you publish it. This document is the reference.

> Scope: the slice as implemented (`Kickl.Core/Games`, `BrowserWindow.Games.cs`). A page can carry a game
> as a sealed WebAssembly module and the reader can play it without trusting it.

## 1. Why WebAssembly

The `jttp://` surface has no JavaScript: no DOM, no network, no timers, no clipboard. A game needs
computation and a place to draw, nothing more. A wasm module can do exactly what its host hands it, so the
host hands it almost nothing. Authors can write in Rust, C, Zig, AssemblyScript, TinyGo or raw WAT; the page
ships only the compiled module.

## 2. The contract: ABI `kickl-game-1`

A game **imports nothing**. A module that imports any function, memory, table or global is refused. It must
export:

| Export | Signature | Meaning |
|---|---|---|
| `memory` | memory | the game's linear memory |
| `kickl_abi` | `() -> i32` | must return `1` |
| `init` | `(seed: i32)` | called once; `seed` is chosen by the host |
| `frame` | `(dt_ms, buttons, pointer_x, pointer_y, pointer_down: i32)` | called about 30 times a second |
| `framebuffer` | `() -> i32` | address of a **240 × 160 RGBA8** image (153,600 bytes) in `memory` |
| `save_ptr`, `save_len` | `() -> i32` | optional: a save area of up to 4,096 bytes |

Colours are little-endian `0xAABBGGRR`. Buttons are bit flags: Left 1, Right 2, Up 4, Down 8, A 16, B 32,
Start 64. The pointer is clamped to the image. There is no clock (`dt_ms` is all the game knows about time),
no randomness (use the seed), no network and no files.

**Saves.** If the game exports `save_ptr`/`save_len`, the host writes the reader's previous save into that area
*before* `init`, and reads it back every few seconds and on stop. The save is stored encrypted in the
reader's own profile, per site and game, and nowhere else.

## 3. Limits

Every call runs on a fixed instruction budget (fuel). An endless loop does not freeze the browser; the game
halts and the reader sees the reason. Memory growth is capped. Deep recursion traps.

| | Unclaimed name | Claimed name |
|---|---|---|
| Module size | 512 KiB | 2 MiB |
| Memory | 8 MiB | 32 MiB |
| `init` budget | 100 M instructions | 400 M |
| per `frame` budget | 10 M | 40 M |

A trap or limit halts only that game. Nothing it does reaches the host.

## 4.0 The games folder

A game does not have to live on a page. Put a folder with a `game.wasm` (and an optional `game.json`) in `games/`, in a `games` folder in your data folder, or in
one beside the program, and the browser lists it on its home page. It runs with the limits of an unclaimed name, with its save kept per game in the reader's profile.
See `games/README.md` and `docs/26-game-tutorial.md` §8.1. A game in your own folder can be **shared**: other computers see its card in the arcade and download it from yours or from
any mirror (`docs/28-game-sharing.md`). The rest of this document applies unchanged.

## 4. Putting a game on a page

Put the compiled module in the site's `blobs/` folder (the file name, without extension, is its id) and add
a `game` element:

```xml
<game id="catcher" label="Catcher"/>
```

The module is sealed like any blob and is covered by the registry content hash. The game does not start
until the reader presses **PLAY**. It stops when the page changes, the tab changes, the profile locks or the
window closes. Add `access="protected"` to make the game need the site's access key.

## 5. A first game

`sites/game-lab.custom/src/catcher.wat` is a complete, commented game (about 150 lines). Build it with
either of:

```
wat2wasm catcher.wat -o ../blobs/catcher.wasm                      # WABT
python -c "import wasmtime;open('../blobs/catcher.wasm','wb').write(wasmtime.wat2wasm(open('catcher.wat').read()))"
```

Then publish the site and open `jttp://game-lab.custom/`. In a higher-level language, build for the
`wasm32-unknown-unknown` target with no WASI and no imports, export the functions above, and keep the
framebuffer and save area in static memory. If the browser says "the game asks the host for env.something",
your toolchain emitted an import; turn off the standard library features that need one.

## 5.1 Checking a game before you publish it

```
kickl game-check game.wasm [--script "none*10,start*3,up*40"] [--seed N] [--claimed] [--png out.png]
```

It loads the module with the limits of an unclaimed (or `--claimed`) name, runs it without a window for the frames you scripted, and reports the module size,
the cost of `init`, the average and worst per-frame instruction counts as a share of the budget, whether the game's memory grew while it ran (a game that
allocates every frame will eventually halt), and the final picture as a PNG. The exit code is 2 if the browser would refuse the game and 3 if it halted.

## 6. What this does not protect

A game can burn its whole budget every frame, so a heavy game makes the page feel slow even though it cannot
freeze it. A game can draw anything, including misleading text; the reader should treat a game's pixels like a
page's, not like browser UI. The fuel counts instructions, not wall-clock time, so a very slow host runs the
same game slower. There is no sound yet.
