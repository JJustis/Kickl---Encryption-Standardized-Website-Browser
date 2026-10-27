# Kickl game examples

Four games for the Kickl Browser, written in [AssemblyScript](https://www.assemblyscript.org/) (a TypeScript-like language that compiles to WebAssembly).
They are the code for the tutorial in [`docs/26-game-tutorial.md`](../../docs/26-game-tutorial.md), which explains every step.

| File | What it is |
|---|---|
| `assembly/01-hello.ts` | the smallest game: a gradient and a bouncing square |
| `assembly/02-mover.ts` | keys, pointer, seeded randomness, text and a saved best score |
| `assembly/03-plasma.ts` | a full-screen effect that shows how the instruction budget works |
| `assembly/caverns.ts` | **Crystal Caverns**, a first-person 3D maze game in 15 KB (with `_maze.ts`, `_textures.ts`) |
| `assembly/kickl.ts` | helpers shared by all of them: the picture, `rgb()`, `fillRect()`, a pixel font, a seedable random generator |

```
npm install          # installs the pinned compiler into this folder
npm run build        # builds every game into build/*.wasm and checks that each imports nothing
```

The compiled games are committed in `build/`, so you can run them without installing anything:

```
dotnet run --project ../../src/Kickl.Cli -c Release -- game-check build/caverns.wasm --script "none*20,start*3,up*60" --png caverns.png
```

`npm run build` also installs each game into the browser's **games folder** (`../../games/<id>/game.wasm`) when that folder's `game.json` has a
`"build": "<name>"` field naming it; the browser lists everything in `games/` on its home page.

Files beginning with `_` are modules, not games; `build.mjs` builds every other `.ts` file in `assembly/`.
