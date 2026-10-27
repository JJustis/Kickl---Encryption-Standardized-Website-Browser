// Procedural textures for Crystal Caverns. Nothing is stored in the module: every wall, floor and ceiling is computed from noise
// when the game starts, which is why the whole game fits in a few kilobytes. Each texture is 64 x 64 pixels.
//
// A texel is 0xAABBGGRR like the picture, but here the alpha byte means "glows": 0 = ordinary stone, 255 = emits its own light
// (the runes), so the renderer can leave them bright even in the dark.

export const TS: i32 = 64;           // texture size
export const TEX_STONE: i32 = 0;
export const TEX_MOSS: i32 = 1;
export const TEX_RUNE: i32 = 2;
export const TEX_FLOOR: i32 = 3;
export const TEX_CEIL: i32 = 4;

export const tex = new StaticArray<u32>(TS * TS * 5);

// ---- noise -------------------------------------------------------------------------------

function mix(a: u32): u32 {
  a ^= a >> 16;
  a *= 0x7feb352d;
  a ^= a >> 15;
  a *= 0x846ca68b;
  a ^= a >> 16;
  return a;
}

/** A repeatable random number for a point, 0..255. The same (x, y, salt) always gives the same answer. */
function hash(x: i32, y: i32, salt: u32): i32 {
  return <i32>(mix((<u32>x * 0x9e3779b1) ^ (<u32>y * 0x85ebca6b) ^ salt) & 255);
}

/** Smooth noise that tiles: random values on a grid of `cell`-pixel squares, blended between the corners. 0..255. */
function smooth(x: i32, y: i32, cell: i32, salt: u32): i32 {
  const n = TS / cell;
  const x0 = (x / cell) % n, y0 = (y / cell) % n;
  const x1 = (x0 + 1) % n, y1 = (y0 + 1) % n;
  const fx = ((x % cell) * 256) / cell, fy = ((y % cell) * 256) / cell;
  const a = hash(x0, y0, salt), b = hash(x1, y0, salt), c = hash(x0, y1, salt), d = hash(x1, y1, salt);
  const top = a * (256 - fx) + b * fx;
  const bottom = c * (256 - fx) + d * fx;
  return (top * (256 - fy) + bottom * fy) >> 16;
}

@inline function pack(r: i32, g: i32, b: i32, glow: i32): u32 {
  return (<u32>(glow & 255) << 24) | (<u32>(min(max(b, 0), 255)) << 16) | (<u32>(min(max(g, 0), 255)) << 8) | <u32>(min(max(r, 0), 255));
}

@inline function put(which: i32, x: i32, y: i32, c: u32): void {
  unchecked((tex[which * TS * TS + y * TS + x] = c));
}

// ---- the textures ------------------------------------------------------------------------

function bricks(which: i32, salt: u32, moss: bool): void {
  for (let y = 0; y < TS; y++) {
    const row = y / 16;
    const offset = (row & 1) * 16;
    for (let x = 0; x < TS; x++) {
      const bx = (x + offset) % 32;
      const by = y % 16;
      const id = ((x + offset) / 32) + row * 4;
      let base = 84 + (hash(id, row, salt) & 31);        // each brick has its own shade
      const grain = (hash(x, y, salt + 1) & 15) + smooth(x, y, 8, salt + 2) / 16;
      let r = base + grain, g = base - 4 + grain, b = base - 12 + grain;
      if (by >= 13) { r -= 14; g -= 14; b -= 12; }       // a shadow under each brick
      if (by < 2 || bx < 2) { r = 34 + (grain >> 1); g = 32 + (grain >> 1); b = 38 + (grain >> 1); } // mortar
      if (moss) {
        const m = smooth(x, y, 16, salt + 3) + smooth(x, y, 8, salt + 4) / 2 + (y * 2) / 4 - 40;
        if (m > 150) {
          const k = min((m - 150) * 3, 200);
          r = (r * (255 - k) + 52 * k) >> 8;
          g = (g * (255 - k) + 120 * k) >> 8;
          b = (b * (255 - k) + 44 * k) >> 8;
        }
      }
      put(which, x, y, pack(r, g, b, 0));
    }
  }
}

function runes(which: i32, salt: u32): void {
  for (let y = 0; y < TS; y++) {
    for (let x = 0; x < TS; x++) {
      const grain = (hash(x, y, salt) & 11) + smooth(x, y, 16, salt + 1) / 24;
      let r = 38 + grain, g = 40 + grain, b = 56 + grain;
      // a 4 x 4 grid of carved glyphs, each a 5 x 5 pattern mirrored left to right and chosen by a hash
      const gx = x / 16, gy = y / 16;
      const lx = (x % 16) - 3, ly = (y % 16) - 3;
      let glow = 0;
      if (lx >= 0 && lx < 10 && ly >= 0 && ly < 10) {
        const cx = lx / 2, cy = ly / 2;                 // 5 x 5 cells of 2 x 2 pixels
        const mx = cx < 3 ? cx : 4 - cx;                // mirror
        const bit = (hash(gx * 7 + mx, gy * 5 + cy, salt + 9) & 3) == 0;
        if (bit && (hash(gx, gy, salt + 11) & 3) != 0) {
          glow = 235 - (smooth(x, y, 8, salt + 12) >> 2);
          r = 70; g = 215; b = 255;
        }
      }
      put(which, x, y, pack(r, g, b, glow));
    }
  }
}

function floor(which: i32, salt: u32): void {
  for (let y = 0; y < TS; y++) {
    for (let x = 0; x < TS; x++) {
      const tile = (x / 32) + (y / 32) * 2;
      const grout = (x % 32) < 2 || (y % 32) < 2;
      const grain = (hash(x, y, salt) & 13) + smooth(x, y, 8, salt + 1) / 14;
      const shade = 70 + (hash(tile, 3, salt) & 23);
      let r = shade - 10 + grain, g = shade + grain, b = shade + 14 + grain;
      if (grout) { r = 24; g = 26; b = 32; }
      put(which, x, y, pack(r, g, b, 0));
    }
  }
}

function ceiling(which: i32, salt: u32): void {
  for (let y = 0; y < TS; y++) {
    for (let x = 0; x < TS; x++) {
      const m = smooth(x, y, 16, salt) + smooth(x, y, 8, salt + 1) / 2;
      const grain = hash(x, y, salt + 2) & 9;
      const v = 22 + (m >> 3) + grain;
      put(which, x, y, pack(v - 2, v, v + 10, 0));
    }
  }
}

export function buildTextures(): void {
  bricks(TEX_STONE, 11, false);
  bricks(TEX_MOSS, 23, true);
  runes(TEX_RUNE, 37);
  floor(TEX_FLOOR, 51);
  ceiling(TEX_CEIL, 67);
}
