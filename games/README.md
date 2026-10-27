# The games folder

The Kickl Browser lists every game in this folder on its home page. **To add a game, put a folder here.**

```
games/
  my-game/
    game.wasm       your compiled game (required)
    game.json       how it appears on the home page (optional)
```

`game.json`:

```json
{ "title": "My Game", "tagline": "one line about it", "author": "you", "accent": "#7be0ff", "order": 10 }
```

Every field is optional. Without a title the folder name is used (`my-game` becomes "My Game"); `order` puts smaller numbers first.
The folder name is the game's id: lowercase letters, digits and hyphens, up to 40 characters.

The browser also looks in a `games` folder inside your data folder (so you can add games without touching the install) and in a `games`
folder beside the program. A game found in your data folder hides one with the same name further on.

A game runs in the same sandbox, with the same limits, as a game on a page (no network, no files, no clock; 512 KiB, 8 MiB of memory,
10 million instructions a frame). If a game cannot run, its card says why. Check a game before you drop it in with
`kickl game-check game.wasm`, and see what the browser found with `kickl games`.

**Sharing.** A game you put in your own games folder can be shared with everyone: press **SHARE THIS GAME** on its card in the arcade (or `kickl games publish <id>`).
Other computers then see a dimmed **DOWNLOAD** card for it, and clicking it downloads the game from you, or from anyone who has already downloaded it, after checking it against
your signature. Games you download land in your own games folder and are served onward. See [`docs/28-game-sharing.md`](../docs/28-game-sharing.md).

How to write one: [`docs/26-game-tutorial.md`](../docs/26-game-tutorial.md). The compiled games in `examples/games/build` are installed here by `npm run build`.
