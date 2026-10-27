# 26 — Build a game for Kickl: a tutorial

> For anyone who can write a little code. You do not need to know WebAssembly. By the end you will have built, tested and published a
> game, and you will have read the source of a 3D game that fits in 15 KB. Every number in this tutorial was measured on the code in
> `examples/games`. The reference for the rules is [`docs/22-wasm-games.md`](22-wasm-games.md); this is the walk-through.

**You will build**

| Step | Game | What it teaches | Cost per frame* |
|---|---|---|---|
| 1 | `01-hello` | the five things a game must provide; drawing | 0.48 M instructions |
| 2 | `02-mover` | keys, pointer, seeded randomness, text, a saved score, **not allocating** | 0.56 M |
| 3 | `03-plasma` | the instruction budget; moving work out of the frame | 2.46 M |
| 4 | **Crystal Caverns** | a full 3D game: raycasting, lighting, sprites, particles | ≈ 5 M (worst 6.6 M) |

\*A game on an unclaimed name may use **10 million** instructions per frame; on a claimed name, 40 million. Section 7 shows how to measure it.

Open `jttp://caverns.custom` in the Kickl Browser (the demo profile has it) to see where we are heading.

---

## 1. How a Kickl game works, in one minute

A web page game gets a whole browser: a network, timers, storage, a thousand APIs. A Kickl game gets **a picture and some numbers**, and nothing else. That is
what makes it safe to run a stranger's game: it has nothing to reach with.

```
          the browser                                   your game (a .wasm file)
  ┌──────────────────────────┐                  ┌──────────────────────────────────┐
  │ calls init(seed)  ───────┼─────────────────►│ build your tables, your world     │
  │                          │                  │                                   │
  │ ~30 times a second:      │                  │                                   │
  │ frame(dt, keys, pointer) ┼─────────────────►│ read the input, update, DRAW into │
  │                          │                  │ a 240 x 160 picture in memory     │
  │ reads framebuffer() ◄────┼──────────────────│                                   │
  │ and shows it             │                  └──────────────────────────────────┘
  └──────────────────────────┘     (the game imports NOTHING: no network, files, clock or random numbers)
```

The game **exports** five things; the browser looks for them by name:

| Export | Meaning |
|---|---|
| `memory` | the game's memory (the toolchain does this for you) |
| `kickl_abi() → 1` | "I speak version 1 of this contract" |
| `init(seed)` | called once. `seed` is the only randomness you will ever get |
| `frame(dt, buttons, pointerX, pointerY, pointerDown)` | called about 30 times a second. `dt` is the milliseconds since the last call: your only clock |
| `framebuffer() → address` | where, in `memory`, your 240 × 160 picture lives |

The picture is 38,400 pixels of 4 bytes each, **R, G, B, A** in that order, which as a little-endian number is `0xAABBGGRR`. Buttons are bit flags:
Left 1, Right 2, Up 4, Down 8, A 16, B 32, Start 64.

Two more exports are optional: `save_ptr()` and `save_len()` give up to 4,096 bytes that the browser keeps for the reader (encrypted, in their own
profile) and hands back to you next time, *before* `init`.

There is a **budget**: every call may run a fixed number of instructions (the "fuel"). An endless loop does not freeze the browser; the game just stops, and
the reader sees why. Memory is capped too. The limits are in the reference; the ones to remember: **10,000,000 instructions per frame, 8 MiB of memory, a
512 KiB module** on an unclaimed name.

---

## 2. Set up

You need two things.

1. **Node.js 20 or newer** to compile your game. This tutorial uses AssemblyScript, a TypeScript-like language that compiles straight to WebAssembly.
2. **The .NET 10 SDK**, which you already have if you are building Kickl from source. It gives you `kickl game-check`, the tool that runs your game
   without a window and tells you how much budget it uses.

```
cd examples/games
npm install            # installs the compiler (AssemblyScript, a pinned version) into this folder only
npm run build          # compiles every game in assembly/ into build/*.wasm and checks each has no imports
```

You will see lines like `ok 01-hello.wasm  629 bytes, no imports`. The compiled games are also committed in `examples/games/build/`, so you can try
everything below before you install anything.

Build the checker once:

```
dotnet build src/Kickl.Cli -c Release
```

and in this tutorial `kickl` means `dotnet run --project src/Kickl.Cli -c Release --`.

**What `npm run build` passes to the compiler**, because each flag matters (see `build.mjs`):

| Flag | Why |
|---|---|
| `--runtime stub` | no garbage collector. A game allocates what it needs in `init()`, once |
| `--use abort=` | AssemblyScript's default `abort()` is an *import*, and a game must import nothing. This makes a failed check simply trap |
| `-O3 --shrinkLevel 1 --noAssert` | small and fast |
| `--initialMemory 4 --maximumMemory 128` | 64 KiB pages: start at 256 KiB, never exceed 8 MiB (the unclaimed limit) |

---

## 3. Step 1 — hello: a gradient and a bouncing square

`examples/games/assembly/01-hello.ts` is the whole game:

```ts
import { W, H, rgb, hline, fillRect, framebufferAddress } from "./kickl";

let t: i32 = 0; // milliseconds since the game started: the only "clock" a game has

// ---- the five exports every Kickl game must provide ----
export function kickl_abi(): i32 { return 1; }
export function framebuffer(): i32 { return framebufferAddress(); }

export function init(seed: i32): void {
  t = 0;
}

export function frame(dt: i32, buttons: i32, pointerX: i32, pointerY: i32, pointerDown: i32): void {
  t += dt;

  // A blue-to-purple gradient, one row at a time
  for (let y = 0; y < H; y++) hline(y, 0, W, rgb(20 + y / 3, 10, 60 + y));

  // A square that bounces left and right using nothing but the elapsed time
  const phase = t % 2000;                     // 0..1999
  const span = phase < 1000 ? phase : 2000 - phase;
  fillRect(10 + (span * (W - 40)) / 1000, 60, 20, 20, rgb(255, 220, 80));
}
```

Things to notice:

- **`kickl.ts`** is a small helper file shared by every example (`W`, `H`, the picture `fb`, `rgb()`, `hline()`, `fillRect()`, a pixel font, a random generator).
  Read it; it is short, and it is the only "library" you need.
- **There is no clock.** `t` is the sum of every `dt` you have been given. A game that moves things by `speed × dt` runs at the same speed whatever the frame rate.
- **Everything is redrawn every frame.** The browser keeps the picture you leave in memory, so you can draw only what changed, but drawing it all is simplest.

Build and run it:

```
npm run build
kickl game-check build/01-hello.wasm --script "none*45" --png hello.png
```

```
module      629 bytes (limit 524,288, unclaimed name)
init        3 instructions, 0.0% of the budget
frames run  45
per frame   average 477,415, worst 477,415 instructions, worst is 4.8% of the budget
memory      262,144 bytes, grew by 0 while running
status      still running
picture     hello.png (720x480)
```

Open `hello.png`: that is your game's picture, scaled up 3×. A full-screen gradient and a square cost **0.48 million** instructions, under 5% of the budget.

> **Try it.** Change the colours. Make the square move up and down as well. Make it change colour when `buttons & 16` (the A button) is held.

---

## 4. Step 2 — mover: input, randomness, text and a saved score

`02-mover.ts` is a small real game: collect coins for 20 seconds. Four new things.

**Input.** Buttons arrive as bit flags, so you test them with `&`:

```ts
if (buttons & LEFT)  mx -= 1;
if (buttons & RIGHT) mx += 1;
```

The **pointer** is where the reader's mouse is, in picture pixels (0..239 by 0..159), and `pointerDown` is 1 while a button is held. `02-mover` walks
towards the pointer when it is down. A game that works with keys *and* a pointer works on a keyboard, a mouse, or a touch screen.

**Randomness.** There is none, except the `seed` the browser gives `init()`. So build your own generator from it. `kickl.ts` has a tiny seedable one
(`Rng`); the same seed always gives the same game, which also makes your game easy to test:

```ts
export function init(seed: i32): void {
  rng = new Rng(seed);
  ...
}
const x = 12 + rng.below(W - 24);     // a random whole number, 0 <= n < limit
```

**Saves.** Export `save_ptr()` and `save_len()` pointing at an area in your memory. The browser fills it with the reader's previous save *before* `init`, and
reads it back every few seconds and when the game stops:

```ts
const save = new StaticArray<u8>(4);
export function save_ptr(): i32 { return <i32>changetype<usize>(save); }
export function save_len(): i32 { return 4; }
...
best = <i32>load<u32>(changetype<usize>(save));                       // read it in init()
store<u32>(changetype<usize>(save), <u32>best);                       // write it when it changes
```

The save is stored encrypted in the reader's own profile, per site and per game, and nowhere else. Up to 4,096 bytes.

**Text.** `kickl.ts` includes a 3 × 5 pixel font: `text("SCORE ", x, y, color)` and `number(n, x, y, color)`.

### The most important lesson in this tutorial: do not allocate in `frame()`

The first version of `02-mover` drew its score with `"SCORE " + score.toString()`. It worked. It also made a new string **every frame**, and a Kickl game has
no garbage collector (`--runtime stub`): memory that is allocated is never freed. At 30 frames a second the game would slowly eat its 8 MiB cap and, after a
long session, **halt**. That is why `kickl.ts` has `number()`, which draws digits straight onto the picture without building a string.

The rule: **build every array, table and string in `init()`; in `frame()`, only read and write what already exists.** The checker watches for you:

```
memory      262,144 bytes, grew by 0 while running
```

If a long run says anything but `grew by 0`, your game allocates while it runs and `kickl game-check` prints an arrow telling you so. The test-suite
checks every example for this.

> **Try it.** Change `ROUND_MS`. Add a second coin colour worth three points. Make the player a different shape.

---

## 5. Step 3 — plasma: understanding the budget

`03-plasma.ts` fills the whole screen every frame with an animated "plasma": each of the 38,400 pixels is the sum of four waves. It is the classic way
to show off a computer, and it is a perfect budget lesson.

```
kickl game-check build/03-plasma.wasm --script "none*30"
```

```
init        1,733,829 instructions, 1.7% of the budget
per frame   average 2,460,733 instructions, worst is 24.6% of the budget
```

2.46 million instructions for 38,400 pixels is about **64 instructions per pixel**, a quarter of the whole budget for one full-screen effect. The budget is
generous, but it is not endless, and a full-screen loop is where you spend it. Three ideas keep the cost down, all used in this file:

1. **Move work out of `frame()` and into `init()`.** `init` has ten times the budget (100 M) and runs once. `03-plasma` computes a sine table, three colour
   palettes and a distance-from-centre table in `init`, so the per-pixel loop does only table lookups:

   ```ts
   const v = <i32>load<u8>(sin + ((x * 2 + t1 + shiftX) & 255))
           + rowWave
           + <i32>load<u8>(sin + (((x + yy) * 2 + t3) & 255))
           + <i32>load<u8>(sin + ((<i32>load<u8>(d + y * W + x) + t1) & 255));
   store<u32>(out, load<u32>(pal + ((v >> 2) << 2)));
   ```

2. **Walk a pointer instead of computing `y * W + x`.** `out += 4` is one operation; an index multiply is several. `kickl.ts`'s `clear()` and `hline()` do this,
   which is why step 1 costs 0.48 M and not 1 M.
3. **Do per-row work per row, not per pixel.** Anything that is the same along a row (here `rowWave`) is computed once per row.

Fuel counts *instructions*, not time, so two computers that run your game get the same answer.

> **Try it.** Press A to change palettes (it is wired up). Then delete one of the four terms of the sum and watch the number drop in `game-check`.

---

## 6. Step 4 — Crystal Caverns: a tour of a 3D game in 15 KB

`caverns.ts` (plus `_maze.ts` and `_textures.ts`) is a first-person 3D game with textured walls, a floor and ceiling, torchlight, coloured crystal light,
sprites, particles, a mini-map and a pixel font, in a 15 KB module that imports nothing. It is "software 3D": no graphics card, no library, just arithmetic into
a 240 × 160 picture. Here is what each part does. Open the files and read along; the comments explain the details.

### The game itself

Find the eight crystals, then walk into the exit portal that appears. The cave is generated from the seed. Your best time is saved. Three **states**: the
title screen (the camera slowly turns behind it), playing, and the win screen.

### 6.1 The cave (`_maze.ts`)

A 25 × 25 grid; each cell is open floor (`0`) or a wall (`1`, `2`, `3` = stone, mossy stone, rune stone). The maze is made with a **depth-first
"recursive backtracker"**: start in a cell, repeatedly walk to an unvisited neighbour two squares away knocking out the wall between, and back up when stuck.
It always gives a perfect maze, so a few extra walls are knocked out at random to create loops. A **breadth-first search** then finds the walking distance from
the start to every cell: the exit goes in the farthest room, and the crystals are spread out with a minimum distance between them. Everything comes from the
seed.

### 6.2 Textures out of nothing (`_textures.ts`)

There are no image files. Five 64 × 64 textures (stone bricks, mossy bricks, glowing runes, flagstones, rock ceiling) are *computed* in `init()` from a
hash-based noise function: `hash(x, y, salt)` gives a repeatable random number for a point, and `smooth()` blends them into soft blobs (that is where the moss
and the grain come from). The alpha byte of a texel means "this glows", so the rune texels light themselves.

### 6.3 One ray per column: the raycaster (`drawWalls`)

The picture is 240 columns wide. For each column the game shoots a **ray** from the player through that column of the screen and walks it across the grid, hopping from
grid line to grid line (the *DDA* algorithm) until it lands in a wall cell:

```
        wall cell
          ┌──┐
          │##│ ◄── the ray stops here
          └──┘
     ╲   .
      ╲ .          for every one of the 240 columns:
       ╲            1. aim a ray            4. the wall is lineH = 160 / distance pixels tall
    ray ╲           2. hop along the grid   5. draw that column of texture, stretched to lineH
         ●  eye     3. note the distance    6. remember the distance in zbuf[x]
```

Far walls are short, near walls are tall: that is the whole 3D effect. The distance is the *perpendicular* distance to the camera plane, which avoids the
fish-eye look. The ray's hit position along the wall decides which column of the texture to use. `zbuf[x]` (the distance in each column) is kept so that sprites
can later hide behind walls.

### 6.4 The floor and ceiling (`drawFloorAndCeiling`)

Floor is drawn **row by row**: every pixel in a row of the lower half is at the same distance from you, `rowDist = 80 / (y − 80)`. So the game works out where the
leftmost pixel of the row lands on the floor and how far along the floor each step to the right moves, then just *adds* for each pixel. The positions are
**16.16 fixed point** (whole numbers where the low 16 bits are the fraction), because adding whole numbers is cheaper than adding fractions here. The ceiling
is the same row mirrored, sampled from the same position, so each loop iteration draws a floor pixel and a ceiling pixel.

### 6.5 Light (`wallLight`)

Each wall column is lit by:

- **the torch**: bright near you, fading with the square of distance, and it *flickers* (two sine waves added);
- **every crystal still in the cave**: a pool of its own colour (cyan, magenta, amber, green) that fades with distance from the *wall point* to the crystal, and
  pulses;
- **the exit portal**, once it is open: green-white;
- a darker shade for walls that face the other way, and a vignette (the edges of the picture are darker, from a table built in `init`).

The result is three numbers per column (red, green, blue light, where 256 is full brightness). The inner loop multiplies each texel channel by them. Because the
light is capped at 256, a texel times the light can never overflow a colour channel, so **the inner loop needs no clamping** at all: a deliberate trade
(crystals cannot "over-expose" a wall) that made the game cost much less.

### 6.6 Sprites and particles (`drawSprites`)

Crystals, the portal and the sparks are *billboards*: flat things that always face you. For each one the game:

1. transforms its position into camera space (a little 2 × 2 matrix maths) to get how far *in front* it is and how far *to the side*;
2. works out its size on screen (`160 / distance`) and its screen column;
3. **sorts** all sprites far to near (insertion sort; a few dozen at most) and draws them in that order, so near ones cover far ones;
4. draws column by column, **skipping any column where a wall is nearer** (`trY >= zbuf[x]`): that is how a crystal hides behind a corner.

A crystal is a faceted kite (left half darker than right, a bright centre ridge, a highlight) with a soft **halo that adds light** to the pixels behind it. The
portal uses a **pseudo-angle** (`y / (|x| + |y|)`) instead of a real `atan2` to draw a spiral without any trigonometry per pixel. Particles are tiny
squares with position, velocity and gravity in 3D; they bounce on the floor and vanish in walls. Picking up a crystal bursts 26 of them.

### 6.7 The interface

The HUD is drawn straight onto the picture: eight gem icons that fill in, a timer, a mini-map of the cells around you with blinking crystal markers and an
arrow for you, and messages. `number()` draws digits with no string allocation (Step 2's lesson). The title and win screens darken the picture behind them
with `dim()` (shift every channel right by one bit).

### 6.8 Keeping inside the budget: how it went from 7.0 M to 4.6 M

The first working version cost **7.0 M** instructions a frame, 73% of the budget with the worst case closer than comfortable. To see why, the stages were
switched off one at a time and the cost measured:

| Switched off | Cost per frame |
|---|---|
| nothing | 7.0 M |
| floor and ceiling | 3.7 M (so they cost **3.3 M**) |
| walls | 3.2 M (so they cost **3.8 M**) |
| sprites (none in view) | 6.7 M |
| the HUD | 6.5 M |

Both big stages were doing needless work *for every pixel*, so the fixes were all in the inner loops:

- the light is capped once per column / row, so there is **no `min()` clamp on every channel**;
- the texture position steps in **16.16 fixed point** instead of floating point;
- glowing runes get **their own loop**, so ordinary stone does not pay for a branch on every pixel;
- the per-pixel "haze" added to the distance fog was dropped (the falloff already darkens distance);
- close-up sprites have a **size cap**, so a crystal at your nose cannot cost more than one at arm's length.

Result: **4.6 M** on average, **6.6 M** worst over thousands of frames of random play. The lesson is the same as Step 3: find the loop that runs 38,400 times
and make it do less.

---

## 7. Measuring and testing your game

`kickl game-check` loads your module the way the browser would and runs it without a window:

```
kickl game-check <game.wasm> [--script STEPS] [--seed N] [--claimed] [--png FILE] [--scale N]
```

| Option | Meaning |
|---|---|
| `--script` | what the "player" does, as comma-separated steps `keys*frames`. Keys are `left right up down a b start`, joined with `+`; `none` presses nothing. A step may end in `@x:y` or `@x:y:down` to place the pointer. Example: `none*10,start*3,up+right*40,a@120:80:down*5` |
| `--seed N` | the seed passed to `init` (the default is 1) |
| `--claimed` | test against the limits of a claimed name (2 MiB, 32 MiB, 40 M per frame) instead of an unclaimed one |
| `--png FILE` | save the last frame, scaled `--scale` times (default 3) |

It prints how big the module is, what `init` cost, the average and **worst** frame cost as a share of the budget, the save area size, whether memory grew, and
whether the game halted. The exit code is non-zero if the browser would refuse the game (2) or the game halted (3).

**Rules of thumb**

- Keep the **worst** frame under about **70%** of the budget. A frame that spikes (an explosion of particles, a crowd on screen) must still fit.
- Run a **long, varied script** (a thousand frames or more) so that you meet the expensive moments, and run it with **several seeds**.
- Make sure `memory ... grew by 0`.
- Test a game's *behaviour* by comparing pictures: the repository's tests (`tests/Kickl.Core.Tests/GameExampleTests.cs`) run each game twice with the same seed
  and check the pictures match, then press a key and check the picture changes. You can copy that approach.

---

## 8. Publish it

There are two ways to put a game in front of people. The first is the one to use.

### 8.1 The quick way: the games folder

Drop a folder into `games/` and the Kickl Browser lists it on its home page, in the arcade, the next time the home page is shown. No page, no publishing:

```
games/
  my-game/
    game.wasm       your compiled game (required)
    game.json       how it appears (optional)
```

```json
{ "title": "My Game", "tagline": "one line about it", "author": "you", "accent": "#7be0ff", "order": 10 }
```

Every field is optional; without a title the folder name is used (`my-game` becomes "My Game"), and `order` puts smaller numbers first. The folder name is the
game's id: lowercase letters, digits and hyphens. Clicking the card opens a game tab and runs it in the sandbox, with the same limits as a game on a page, and its
save is kept, encrypted, in the reader's profile.

The browser looks in three places, most personal first: a `games` folder inside **your data folder** (add games without touching the install), a `games`
folder beside the program, and the `games` folder of a source checkout. A game found in an earlier place hides the same id in a later one.

If the tutorial's toolchain built your game, add `"build": "my-game"` to its `game.json` (the name of the file in `assembly/`, without `.ts`) and `npm run build`
installs the fresh module into the folder for you; that is how `games/crystal-caverns/` is kept up to date. `kickl games` shows what the browser found and why a game
was refused (too big, empty):

```
kickl games
kickl game-check games/my-game/game.wasm --script "none*10,start*3,up*40"
```

The repository's test-suite loads every game in `games/` and checks that it runs, stays under 80% of the budget and does not allocate, so a broken game cannot be
committed unnoticed.

### 8.1.1 Share it with everyone

A game in **your own** games folder (`<your data folder>/games/my-game/`) can be shared: press **SHARE THIS GAME** on its arcade card, or `kickl games publish my-game --pin YOUR-PIN`.
That signs a small card for your game and puts it in the registry. From then on every computer running Kickl shows a dimmed **DOWNLOAD** card for it in the arcade, and
clicking one downloads the game from your computer (while the browser is open) or from anyone who has already downloaded it, after checking it against your signature.
Every download makes one more computer that can pass it on. Publish again after you change the file and the card's version goes up. The full story, including what
this does and does not protect, is in [`docs/28-game-sharing.md`](28-game-sharing.md).

### 8.2 On a page

A game can also live on a page, sealed with the page's other content (this is how a game is **sold**, see below). Make a site folder:

```
sites/mygame.custom/
  site.kml                the page
  blobs/mygame.wasm       your compiled game; its id is the file name without the extension
```

`site.kml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<kml v="1" site="mygame.custom" epoch="1">
  <head>
    <title>My game</title>
    <meta name="headline" content="A one-line description of the game."/>
    <meta name="tags" content="games, wasm, demo"/>
  </head>
  <body>
    <page>
      <h1>My game</h1>
      <p>Arrow keys move. Press play.</p>
      <game id="mygame" label="My game"/>
    </page>
  </body>
</kml>
```

Publish and open it:

```
kickl publish --site sites/mygame.custom --host mygame.custom --pin YOUR-PIN
kickl open jttp://mygame.custom/ --pin YOUR-PIN
```

Then start the Kickl Browser, sign in, open `jttp://mygame.custom/` and press **PLAY**. The game is sealed in the page like any other file, covered by the page's
content hash, and **does not start until the reader presses PLAY**. It stops when they leave the page, change tab, lock the profile or close the window.

- **Which to choose.** The games folder is for games that are simply there to be played. Put a game on a page when it belongs with text, when the page's
  owner should be shown as the author, or when you want to **sell** it.
- **Selling a game.** Put `access="protected"` on the `<game>` and the module only opens with an access key. Combine it with a `<shop>` (see
  [`docs/27-shop-syntax.md`](27-shop-syntax.md)) and you can sell a game for Bitcoin and send the buyer the key.
- **Bigger games.** A *claimed* name (paid for at the BTC shop) may run games of up to 2 MiB with 32 MiB of memory and 40 M instructions a frame.

---

## 9. Things that will bite you (all of them bit while building this)

| Symptom | Cause and fix |
|---|---|
| The browser says "the game asks the host for env.abort" (or any `env.` name) | Your toolchain emitted an *import*. With AssemblyScript, build with `--use abort=` and `--runtime stub` (`npm run build` does). A game may import nothing. |
| `ERROR AS200: Conversion from type 'f64' to 'f32'` | AssemblyScript will not mix whole numbers, 32-bit and 64-bit fractions silently. A bare `2.0 * x` or `1e30` is a 64-bit float. Annotate (`const a: f32 = …`) or cast (`<f32>1e30`). |
| `ERROR TS2365: Operator '*' cannot be applied to types 'i32' and 'f32'` | Same family: write `127.0` not `127`, or use a helper like `wave()` in `03-plasma.ts`. |
| `Cannot find name 'clamp'` | AssemblyScript has `min` and `max` but no `clamp`: `max(lo, min(x, hi))`. |
| The game halts after a long time | It allocates every frame (a string, an array, `new`). Run `game-check` for a long script and look at `grew by`. Build everything in `init()`. |
| The game halts at once with "fuel" | A frame ran past its budget. Find the per-pixel loop and make it cheaper (Step 3), or lower the work (fewer sprites, smaller halos). |
| Pictures are upside-down or the colours are wrong | The picture is **R, G, B, A** bytes. `0xAABBGGRR` as a number. `rgb(r, g, b)` in `kickl.ts` does it. |
| A division traps | Integer division by zero traps in WebAssembly. Guard the divisor. |
| Different results from the same seed | You used something that is not a function of the seed and the inputs (an uninitialised global, `Date`). There is no clock and no randomness, so it should not be possible; check your own state resets in `init()`. |

---

## 10. Other languages

Anything that compiles to **WebAssembly with no imports** works: Rust (`wasm32-unknown-unknown`, `#![no_std]`), C and C++ (clang with `--target=wasm32 -nostdlib`),
Zig, TinyGo, or raw WAT text (see `sites/game-lab.custom/src/catcher.wat`, a complete 150-line game). The recipe is always the same: export `memory`, `kickl_abi`, `init`,
`frame` and `framebuffer`; keep the 240 × 160 × 4 byte picture in static memory; turn off anything that needs the operating system (a standard library, WASI, an
allocator you do not need). If the browser reports an import, a library you linked is asking the host for something. Everything in this tutorial about budget,
allocation and testing applies unchanged; only the syntax differs. (The AssemblyScript examples are the ones built and tested here.)

---

## 11. Cheat sheet

```
exports   memory · kickl_abi() -> 1 · init(seed) · frame(dt, buttons, px, py, pdown) · framebuffer() -> addr
          optional: save_ptr() -> addr · save_len() -> n          (n <= 4096)
picture   240 x 160 x 4 bytes, R G B A, = 0xAABBGGRR            buttons  Left 1  Right 2  Up 4  Down 8  A 16  B 32  Start 64
budget    unclaimed: 512 KiB module · 8 MiB memory · init 100 M · frame 10 M      claimed: 2 MiB · 32 MiB · 400 M · 40 M
no        imports · clock (use dt) · randomness (use the seed) · network · files · sound (not yet)
kickl.ts  W H fb rgb() px() clear() hline() fillRect() text() number() glyph() Rng
```

**Where to go next.** Change something in Crystal Caverns: make the torch burn down so you must hurry; add a second kind of pickup; make the exit move; put
the saved best time on the title screen in a different colour. Or write a game from scratch: a platformer needs gravity and tile collision, a puzzle game
needs only a grid and input. The constraints (a small picture, a small budget, no outside world) make small games finish.

Files: [`examples/games/`](../examples/games) · the showcase site [`sites/caverns.custom/`](../sites/caverns.custom) · the checker
[`src/Kickl.Core/Games/GameCheck.cs`](../src/Kickl.Core/Games/GameCheck.cs) · the tests
[`tests/Kickl.Core.Tests/GameExampleTests.cs`](../tests/Kickl.Core.Tests/GameExampleTests.cs).
