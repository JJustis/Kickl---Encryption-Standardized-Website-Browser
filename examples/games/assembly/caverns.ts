// CRYSTAL CAVERNS — a first-person 3D maze, drawn entirely in software inside the Kickl sandbox.
//
// Find all eight crystals, then reach the exit portal. Every cave is different (it grows from the seed).
//   Left / Right ... turn          Up / Down ... walk          A / B ... sidestep          Start ... begin
//   Pointer held down: steer towards the pointer, walk forward (pointer low on the screen walks backwards)
//
// What is on show, with no imports, no clock and no JavaScript:
//   * a raycaster that casts one ray per screen column, with textured walls
//   * perspective-correct textured floor and ceiling
//   * torchlight that flickers, and every crystal lights the walls near it in its own colour
//   * depth-sorted glowing sprites (crystals, an exit portal) and bursts of 3D particles
//   * a procedural maze, procedural textures, a mini-map, a pixel font and a best time saved in your own profile
// The whole game is a few kilobytes: nothing is stored that can be computed.
import {
  W, H, fb, rgb, fillRect, text, textWidth, number, Rng,
  LEFT, RIGHT, UP, DOWN, BTN_A, BTN_B, START, framebufferAddress
} from "./kickl";
import { tex, TS, TEX_FLOOR, TEX_CEIL, buildTextures } from "./_textures";
import {
  MW, MH, NCRYSTALS, map, generate, cell, startX, startY, exitX, exitY, crystalX, crystalY
} from "./_maze";

// ---- the exports the browser looks for ----------------------------------------------------
export function kickl_abi(): i32 { return 1; }
export function framebuffer(): i32 { return framebufferAddress(); }

// The save area: [0] a magic number, [1] the best time in milliseconds (0 = none yet), [2] how many caves you have escaped.
const save = new StaticArray<u32>(3);
const SAVE_MAGIC: u32 = 0x3156434b;   // "KCV1"
export function save_ptr(): i32 { return <i32>changetype<usize>(save); }
export function save_len(): i32 { return 12; }

// ---- constants and state ------------------------------------------------------------------
const HALF: i32 = 80;                       // the horizon row
const TITLE: i32 = 0, PLAYING: i32 = 1, WON: i32 = 2;
const MAXP: i32 = 56;                       // particles
const SPRITES: i32 = NCRYSTALS + 1 + MAXP;  // crystals + the portal + particles

let rng: Rng = new Rng(1);
let state: i32 = TITLE;
let clock: i32 = 0;           // milliseconds since the game started: drives flicker and animation
let gameMs: i32 = 0;          // time spent inside the cave this run
let flash: i32 = 0;           // white flash after picking up a crystal, 0..255
let msgMs: i32 = 0;           // how long the on-screen message still has
let msgCode: i32 = 0;         // which message: 1 crystal collected, 2 exit open, 3 hurry
let startHeld: bool = true;
let newBest: bool = false;

let px: f32 = 1.5, py: f32 = 1.5, ang: f32 = 0;
let dirX: f32 = 1, dirY: f32 = 0, planeX: f32 = 0, planeY: f32 = 0.66;

const alive = new StaticArray<u8>(NCRYSTALS);
let collected: i32 = 0;
let portalOpen: bool = false;

// particles: position, velocity, remaining life (ms) and colour
const ppx = new StaticArray<f32>(MAXP), ppy = new StaticArray<f32>(MAXP), ppz = new StaticArray<f32>(MAXP);
const pvx = new StaticArray<f32>(MAXP), pvy = new StaticArray<f32>(MAXP), pvz = new StaticArray<f32>(MAXP);
const plife = new StaticArray<i32>(MAXP);
const pcol = new StaticArray<u32>(MAXP);
let pnext: i32 = 0;

// Look-up tables built once in init(): trigonometry per pixel would burn the instruction budget
const sinT = new StaticArray<u8>(256);
const colV = new StaticArray<i32>(W);     // darkening towards the left and right edges (a cheap vignette)
const rowV = new StaticArray<i32>(H);     // ... and towards the top and bottom
const zbuf = new StaticArray<f32>(W);     // distance of the wall in each column, so sprites can hide behind walls

// Crystal colours: cyan, magenta, amber, green
const GEM_R: StaticArray<i32> = [80, 255, 255, 110];
const GEM_G: StaticArray<i32> = [220, 90, 190, 255];
const GEM_B: StaticArray<i32> = [255, 230, 60, 140];

// The light falling on the thing being drawn, in 1/256ths (256 = full brightness), set per column or per row
let lr: i32 = 0, lg: i32 = 0, lb: i32 = 0;
let flicker: f32 = 1;

// Sprite sorting scratch
const sIdx = new StaticArray<i32>(SPRITES);
const sDist = new StaticArray<f32>(SPRITES);

// ---- setup --------------------------------------------------------------------------------

function newCave(): void {
  generate(rng);
  px = <f32>startX + 0.5;
  py = <f32>startY + 0.5;
  ang = 0;
  // face along the first open corridor so the first thing you see is a view, not a wall
  if (cell(startX + 1, startY) != 0 && cell(startX, startY + 1) == 0) ang = 1.5707964;
  for (let i = 0; i < NCRYSTALS; i++) unchecked((alive[i] = 1));
  collected = 0;
  portalOpen = false;
  gameMs = 0;
  flash = 0;
  msgMs = 0;
  newBest = false;
  for (let i = 0; i < MAXP; i++) unchecked((plife[i] = 0));
}

export function init(seed: i32): void {
  if (save[0] != SAVE_MAGIC) { save[0] = SAVE_MAGIC; save[1] = 0; save[2] = 0; }   // the browser restored a save? keep it
  rng = new Rng(seed);
  buildTextures();
  for (let i = 0; i < 256; i++) unchecked((sinT[i] = <u8>(127.5 + 127.5 * Mathf.sin(<f32>i * (Mathf.PI * 2.0 / 256.0)))));
  for (let x = 0; x < W; x++) {
    const d = x - W / 2;
    unchecked((colV[x] = 256 - (d * d) / 190));        // 256 in the middle, ~ 180 at the edges
  }
  for (let y = 0; y < H; y++) {
    const d = y - H / 2;
    unchecked((rowV[y] = 256 - (d * d) / 90));
  }
  newCave();
  state = TITLE;
}

// ---- helpers ------------------------------------------------------------------------------

@inline function wallAt(x: f32, y: f32): bool {
  return cell(<i32>x, <i32>y) != 0;
}

function spawn(x: f32, y: f32, z: f32, vx: f32, vy: f32, vz: f32, life: i32, c: u32): void {
  const i = pnext;
  pnext = (pnext + 1) % MAXP;
  unchecked((ppx[i] = x)); unchecked((ppy[i] = y)); unchecked((ppz[i] = z));
  unchecked((pvx[i] = vx)); unchecked((pvy[i] = vy)); unchecked((pvz[i] = vz));
  unchecked((plife[i] = life)); unchecked((pcol[i] = c));
}

function burst(x: f32, y: f32, z: f32, r: i32, g: i32, b: i32, count: i32, speed: f32): void {
  for (let k = 0; k < count; k++) {
    const a: f32 = rng.unit() * 6.2831855;
    const up: f32 = rng.unit() * 0.9 + 0.2;
    const s: f32 = (0.4 + rng.unit()) * speed;
    spawn(x, y, z, Mathf.cos(a) * s, Mathf.sin(a) * s, up * speed * 0.8, 700 + rng.below(700),
      rgb(min(255, r + rng.below(60)), min(255, g + rng.below(60)), min(255, b + rng.below(60))));
  }
}

function setMessage(code: i32, ms: i32): void { msgCode = code; msgMs = ms; }

// ---- the world's light --------------------------------------------------------------------

/** The light falling on a wall point (hx, hy) at distance d from the eye. Crystals glow in their own colour. */
function wallLight(hx: f32, hy: f32, d: f32, side: i32, column: i32): void {
  const torch = 330.0 * flicker / (1.0 + d * d * 0.32);
  let r = <i32>(torch) + 10;
  let g = <i32>(torch * 0.80) + 14;
  let b = <i32>(torch * 0.58) + 26;
  const t = <f32>clock;
  for (let i = 0; i < NCRYSTALS; i++) {
    if (unchecked(alive[i]) == 0) continue;
    const dx = hx - (<f32>unchecked(crystalX[i]) + 0.5), dy = hy - (<f32>unchecked(crystalY[i]) + 0.5);
    const d2 = dx * dx + dy * dy;
    if (d2 > 30.0) continue;                      // too far to matter
    const k = (420.0 + 70.0 * Mathf.sin(t * 0.004 + <f32>i * 1.7)) / (1.0 + d2 * 3.0);
    const c = i & 3;
    r += <i32>(k * <f32>unchecked(GEM_R[c]) * 0.0039);
    g += <i32>(k * <f32>unchecked(GEM_G[c]) * 0.0039);
    b += <i32>(k * <f32>unchecked(GEM_B[c]) * 0.0039);
  }
  if (portalOpen) {                               // the open exit casts a green-white glow
    const dx = hx - (<f32>exitX + 0.5), dy = hy - (<f32>exitY + 0.5);
    const k = 520.0 / (1.0 + (dx * dx + dy * dy) * 2.4);
    r += <i32>(k * 0.55); g += <i32>k; b += <i32>(k * 0.8);
  }
  if (side == 1) { r = (r * 190) >> 8; g = (g * 190) >> 8; b = (b * 190) >> 8; }   // walls facing the other way are darker
  // the light never exceeds 256 (full brightness): the inner loops rely on that to skip clamping
  const v = unchecked(colV[column]);
  lr = min((r * v) >> 8, 256);
  lg = min((g * v) >> 8, 256);
  lb = min((b * v) >> 8, 256);
}

// ---- drawing the world --------------------------------------------------------------------

function drawFloorAndCeiling(): void {
  const floorBase = changetype<usize>(tex) + <usize>(TEX_FLOOR * TS * TS * 4);
  const ceilBase = changetype<usize>(tex) + <usize>(TEX_CEIL * TS * TS * 4);
  const out = changetype<usize>(fb);
  const x0 = dirX - planeX, y0 = dirY - planeY;

  for (let y = HALF + 1; y < H; y++) {
    const rowDist: f32 = <f32>HALF / <f32>(y - HALF);                 // how far away this row of floor is
    // The light on this whole row of floor and ceiling. It never exceeds 256, so a texel times the light can never overflow
    // a colour channel and the inner loop needs no clamping at all.
    const torch: f32 = 300.0 * flicker / (1.0 + rowDist * rowDist * 0.30);
    const v = unchecked(rowV[y]);
    const r = min(((<i32>(torch) + 8) * v) >> 8, 256);
    const g = min(((<i32>(torch * 0.80) + 12) * v) >> 8, 256);
    const b = min(((<i32>(torch * 0.58) + 22) * v) >> 8, 256);

    // 16.16 fixed point: where the leftmost pixel of this row lands on the floor, and how far each pixel to the right moves it
    let fx = <i32>((px + rowDist * x0) * 65536.0);
    let fy = <i32>((py + rowDist * y0) * 65536.0);
    const sx = <i32>(rowDist * planeX * (4.0 / <f32>W) * 65536.0);
    const sy = <i32>(rowDist * planeY * (4.0 / <f32>W) * 65536.0);

    let floorPtr = out + <usize>((y * W) * 4);
    let ceilPtr = out + <usize>(((H - 1 - y) * W) * 4);
    for (let x = 0; x < W; x++) {
      const t = ((((fy >> 10) & 63) << 6) + ((fx >> 10) & 63)) << 2;
      fx += sx;
      fy += sy;
      const f = load<u32>(floorBase + <usize>t);
      const c = load<u32>(ceilBase + <usize>t);
      store<u32>(floorPtr, 0xff000000 | (<u32>(((f >> 16) & 255) * b >> 8) << 16) | (<u32>(((f >> 8) & 255) * g >> 8) << 8) | <u32>((f & 255) * r >> 8));
      store<u32>(ceilPtr, 0xff000000 | (<u32>(((c >> 16) & 255) * b >> 8) << 16) | (<u32>(((c >> 8) & 255) * g >> 8) << 8) | <u32>((c & 255) * r >> 8));
      floorPtr += 4;
      ceilPtr += 4;
    }
  }
}

function drawWalls(): void {
  const out = changetype<usize>(fb);
  const texBase = changetype<usize>(tex);

  for (let x = 0; x < W; x++) {
    // One ray per column: from the eye, through this column of the screen
    const camera: f32 = <f32>x * 2.0 / <f32>W - 1.0;
    const rdx: f32 = dirX + planeX * camera;
    const rdy: f32 = dirY + planeY * camera;
    let mapX = <i32>px, mapY = <i32>py;
    const ddx: f32 = rdx == 0 ? <f32>1e30 : abs<f32>(1.0 / rdx);   // how far along the ray to the next vertical grid line
    const ddy: f32 = rdy == 0 ? <f32>1e30 : abs<f32>(1.0 / rdy);   // ... and the next horizontal one
    let stepX: i32, stepY: i32;
    let sideX: f32, sideY: f32;
    if (rdx < 0) { stepX = -1; sideX = (px - <f32>mapX) * ddx; } else { stepX = 1; sideX = (<f32>mapX + 1.0 - px) * ddx; }
    if (rdy < 0) { stepY = -1; sideY = (py - <f32>mapY) * ddy; } else { stepY = 1; sideY = (<f32>mapY + 1.0 - py) * ddy; }

    // DDA: hop from grid line to grid line until the ray lands in a wall
    let side = 0;
    let hit = 0;
    for (let guard = 0; guard < 64 && hit == 0; guard++) {
      if (sideX < sideY) { sideX += ddx; mapX += stepX; side = 0; } else { sideY += ddy; mapY += stepY; side = 1; }
      hit = cell(mapX, mapY);
    }
    if (hit == 0) { unchecked((zbuf[x] = 99.0)); continue; }

    const perp: f32 = max<f32>(0.05, side == 0 ? sideX - ddx : sideY - ddy);
    unchecked((zbuf[x] = perp));
    const hx = px + rdx * perp, hy = py + rdy * perp;
    let wallX: f32 = side == 0 ? hy : hx;
    wallX -= Mathf.floor(wallX);
    let tx = <i32>(wallX * <f32>TS);
    if ((side == 0 && rdx > 0) || (side == 1 && rdy < 0)) tx = TS - 1 - tx;   // mirror so the texture reads the right way round

    wallLight(hx, hy, perp, side, x);
    const lineH = <i32>(<f32>H / perp);
    let y0 = HALF - lineH / 2;
    const y1 = min(HALF + lineH / 2, H);
    // How far to move down the texture for each pixel down the screen, in 16.16 fixed point (whole-number maths is cheaper)
    const step = <i32>(<f32>(TS * 65536) / <f32>lineH);
    let pos = 0;
    if (y0 < 0) { pos = -y0 * step; y0 = 0; }
    const colBase = texBase + <usize>(((hit - 1) * TS * TS + tx) * 4);
    const r = lr, g = lg, b = lb;

    let p = out + <usize>((y0 * W + x) * 4);
    if (hit == 3) {
      for (let y = y0; y < y1; y++) {                       // runes: some texels glow whatever the light
        const c = load<u32>(colBase + <usize>(((pos >> 16) & 63) << 8));
        pos += step;
        const glow = <i32>(c >> 24);
        const cr = <i32>(c & 255), cg = <i32>((c >> 8) & 255), cb = <i32>((c >> 16) & 255);
        store<u32>(p, 0xff000000 | (<u32>max((cb * b) >> 8, (cb * glow) >> 8) << 16) | (<u32>max((cg * g) >> 8, (cg * glow) >> 8) << 8) | <u32>max((cr * r) >> 8, (cr * glow) >> 8));
        p += <usize>(W * 4);
      }
    } else {
      for (let y = y0; y < y1; y++) {                       // plain stone: three multiplies, no clamps, no branches
        const c = load<u32>(colBase + <usize>(((pos >> 16) & 63) << 8));
        pos += step;
        store<u32>(p, 0xff000000 | (<u32>(((c >> 16) & 255) * b >> 8) << 16) | (<u32>(((c >> 8) & 255) * g >> 8) << 8) | <u32>((c & 255) * r >> 8));
        p += <usize>(W * 4);
      }
    }
  }
}

// ---- sprites ------------------------------------------------------------------------------

@inline function addPixel(p: usize, r: i32, g: i32, b: i32): void {
  const c = load<u32>(p);
  store<u32>(p, 0xff000000
    | (<u32>min(<i32>((c >> 16) & 255) + b, 255) << 16)
    | (<u32>min(<i32>((c >> 8) & 255) + g, 255) << 8)
    | <u32>min(<i32>(c & 255) + r, 255));
}

/** A glowing crystal: a faceted kite shape with a soft halo that adds light to whatever is behind it. */
function drawCrystal(sx: i32, trY: f32, gem: i32, z: f32, pulse: i32): void {
  const hh = min(<i32>(<f32>H * 0.30 / trY), 40);               // half the crystal's height in pixels (capped, so a crystal at your nose costs no more than one at arm's length)
  if (hh < 1) return;
  const cy = HALF + <i32>((0.5 - z) * <f32>H / trY);
  const halo = hh + hh / 2;
  const cr = unchecked(GEM_R[gem]), cg = unchecked(GEM_G[gem]), cb = unchecked(GEM_B[gem]);
  const hh2 = hh * hh;
  const x0 = max(sx - halo, 0), x1 = min(sx + halo, W - 1);
  for (let x = x0; x <= x1; x++) {
    if (trY >= unchecked(zbuf[x])) continue;                      // behind a wall in this column
    const du = x - sx;
    const y0 = max(cy - halo, 0), y1 = min(cy + halo, H - 1);
    let p = changetype<usize>(fb) + <usize>((y0 * W + x) * 4);
    for (let y = y0; y <= y1; y++) {
      const dv = y - cy;
      const au = du < 0 ? -du : du, av = dv < 0 ? -dv : dv;
      if (au * 3 + av * 2 < hh * 2) {
        // the body: left facets darker than right, a bright centre ridge and a highlight
        let k = du < 0 ? 140 : 225;
        if (dv < -hh / 4) k += 20;
        if (au * 14 < hh) k = 255;
        if (du < 0 && dv < 0 && (du + hh / 3) * (du + hh / 3) + (dv + hh / 3) * (dv + hh / 3) < hh2 / 40) k = 300;
        k = (k * pulse) >> 8;
        const rr = min((cr * k) >> 8, 255), gg = min((cg * k) >> 8, 255), bb = min((cb * k) >> 8, 255);
        store<u32>(p, 0xff000000 | (<u32>bb << 16) | (<u32>gg << 8) | <u32>rr);
      } else {
        const d2 = du * du + dv * dv;
        if (d2 < halo * halo) {
          const glow = (110 * hh2 * pulse >> 8) / (hh2 + d2 * 2);    // soft falloff from the crystal's centre
          if (glow > 0) addPixel(p, (cr * glow) >> 8, (cg * glow) >> 8, (cb * glow) >> 8);
        }
      }
      p += <usize>(W * 4);
    }
  }
}

/** The exit portal: a swirling ring of light. A "pseudo angle" (no trigonometry per pixel) makes the spiral arms. */
function drawPortal(sx: i32, trY: f32, tms: i32): void {
  const hh = min(<i32>(<f32>H * 0.45 / trY), 52);
  if (hh < 2) return;
  const cy = HALF + <i32>(0.02 * <f32>H / trY);
  const x0 = max(sx - hh, 0), x1 = min(sx + hh, W - 1);
  const hh2 = hh * hh;
  for (let x = x0; x <= x1; x++) {
    if (trY >= unchecked(zbuf[x])) continue;
    const du = x - sx;
    const y0 = max(cy - hh, 0), y1 = min(cy + hh, H - 1);
    let p = changetype<usize>(fb) + <usize>((y0 * W + x) * 4);
    for (let y = y0; y <= y1; y++) {
      const dv = y - cy;
      const d2 = du * du + dv * dv;
      if (d2 < hh2) {
        const r = (d2 * 255) / hh2;                                  // 0 in the middle .. 255 at the rim
        const au = du < 0 ? -du : du, av = dv < 0 ? -dv : dv;
        let pa = (av * 64) / (au + av + 1);                          // 0..64 across a quarter turn
        if (du < 0) pa = 128 - pa;
        if (dv < 0) pa = 256 - pa;
        const arms = <i32>unchecked(sinT[(pa * 3 + (r >> 1) * 2 - (tms >> 3)) & 255]);
        const ring = r > 200 ? (255 - r) * 4 : 120 + (r >> 2);
        const v = (arms * ring) >> 8;
        addPixel(p, v >> 2, v, (v * 3) >> 2);
      }
      p += <usize>(W * 4);
    }
  }
}

/** A single spark: a small bright square that gets smaller with distance. */
function drawSpark(sx: i32, trY: f32, z: f32, c: u32, fade: i32): void {
  if (sx < 0 || sx >= W || trY >= unchecked(zbuf[sx])) return;
  const size = max(1, <i32>(1.6 / trY));
  const cy = HALF + <i32>((0.5 - z) * <f32>H / trY);
  const r = (<i32>(c & 255) * fade) >> 8, g = (<i32>((c >> 8) & 255) * fade) >> 8, b = (<i32>((c >> 16) & 255) * fade) >> 8;
  for (let yy = 0; yy < size; yy++) {
    for (let xx = 0; xx < size; xx++) {
      const x = sx + xx, y = cy + yy;
      if (<u32>x < <u32>W && <u32>y < <u32>H) addPixel(changetype<usize>(fb) + <usize>((y * W + x) * 4), r, g, b);
    }
  }
}

function drawSprites(): void {
  const inv: f32 = <f32>1.0 / (planeX * dirY - dirX * planeY);
  let n = 0;
  // Work out how far away each sprite is, then draw the far ones first
  for (let i = 0; i < NCRYSTALS; i++) {
    if (unchecked(alive[i]) == 0) continue;
    const dx = <f32>unchecked(crystalX[i]) + 0.5 - px, dy = <f32>unchecked(crystalY[i]) + 0.5 - py;
    unchecked((sIdx[n] = i)); unchecked((sDist[n] = dx * dx + dy * dy)); n++;
  }
  if (portalOpen) {
    const dx = <f32>exitX + 0.5 - px, dy = <f32>exitY + 0.5 - py;
    unchecked((sIdx[n] = NCRYSTALS)); unchecked((sDist[n] = dx * dx + dy * dy)); n++;
  }
  for (let i = 0; i < MAXP; i++) {
    if (unchecked(plife[i]) <= 0) continue;
    const dx = unchecked(ppx[i]) - px, dy = unchecked(ppy[i]) - py;
    unchecked((sIdx[n] = NCRYSTALS + 1 + i)); unchecked((sDist[n] = dx * dx + dy * dy)); n++;
  }
  for (let i = 1; i < n; i++) {                       // insertion sort, farthest first
    const di = unchecked(sDist[i]), ii = unchecked(sIdx[i]);
    let j = i - 1;
    while (j >= 0 && unchecked(sDist[j]) < di) {
      unchecked((sDist[j + 1] = sDist[j])); unchecked((sIdx[j + 1] = sIdx[j]));
      j--;
    }
    unchecked((sDist[j + 1] = di)); unchecked((sIdx[j + 1] = ii));
  }

  for (let s = 0; s < n; s++) {
    const id = unchecked(sIdx[s]);
    let wx: f32, wy: f32, z: f32 = 0.34;
    if (id < NCRYSTALS) {
      wx = <f32>unchecked(crystalX[id]) + 0.5; wy = <f32>unchecked(crystalY[id]) + 0.5;
      z = 0.34 + 0.05 * Mathf.sin(<f32>clock * 0.003 + <f32>id * 2.3);
    } else if (id == NCRYSTALS) {
      wx = <f32>exitX + 0.5; wy = <f32>exitY + 0.5;
    } else {
      const k = id - NCRYSTALS - 1;
      wx = unchecked(ppx[k]); wy = unchecked(ppy[k]); z = unchecked(ppz[k]);
    }
    // into camera space: trX is sideways, trY is how far in front of you
    const rx = wx - px, ry = wy - py;
    const trX: f32 = inv * (dirY * rx - dirX * ry);
    const trY: f32 = inv * (-planeY * rx + planeX * ry);
    if (trY < 0.12) continue;
    const sx = <i32>(<f32>(W / 2) * (1.0 + trX / trY));
    if (id < NCRYSTALS) {
      const pulse = 224 + <i32>(32.0 * Mathf.sin(<f32>clock * 0.006 + <f32>id));
      drawCrystal(sx, trY, id & 3, z, pulse);
    } else if (id == NCRYSTALS) {
      drawPortal(sx, trY, clock);
    } else {
      const k = id - NCRYSTALS - 1;
      drawSpark(sx, trY, z, unchecked(pcol[k]), min(255, unchecked(plife[k]) >> 2));
    }
  }
}

// ---- the heads-up display -----------------------------------------------------------------

/** Darken a rectangle by shifting every colour channel right by `shift` bits (1 = half as bright). */
function dim(x: i32, y: i32, w: i32, h: i32, shift: i32): void {
  const keep = <u32>((255 >> shift) * 0x010101);
  for (let yy = max(y, 0); yy < min(y + h, H); yy++) {
    let p = changetype<usize>(fb) + <usize>((yy * W + max(x, 0)) * 4);
    for (let xx = max(x, 0); xx < min(x + w, W); xx++) {
      store<u32>(p, 0xff000000 | ((load<u32>(p) >> shift) & keep));
      p += 4;
    }
  }
}

function minimap(): void {
  const cx0 = <i32>px, cy0 = <i32>py;
  const R = 6, S = 3;
  const ox = W - (2 * R + 1) * S - 5, oy = 5;
  fillRect(ox - 2, oy - 2, (2 * R + 1) * S + 4, (2 * R + 1) * S + 4, rgb(6, 8, 18));
  for (let j = -R; j <= R; j++) {
    for (let i = -R; i <= R; i++) {
      const c = cell(cx0 + i, cy0 + j);
      fillRect(ox + (i + R) * S, oy + (j + R) * S, S, S, c == 0 ? rgb(22, 30, 52) : rgb(66, 82, 130));
    }
  }
  const blink = ((clock >> 8) & 1) == 0;
  for (let i = 0; i < NCRYSTALS; i++) {
    if (unchecked(alive[i]) == 0) continue;
    const dx = unchecked(crystalX[i]) - cx0, dy = unchecked(crystalY[i]) - cy0;
    if (dx >= -R && dx <= R && dy >= -R && dy <= R) {
      const gem = i & 3;
      fillRect(ox + (dx + R) * S, oy + (dy + R) * S, S, S,
        blink ? rgb(unchecked(GEM_R[gem]), unchecked(GEM_G[gem]), unchecked(GEM_B[gem])) : rgb(40, 50, 70));
    }
  }
  if (portalOpen) {
    const dx = exitX - cx0, dy = exitY - cy0;
    if (dx >= -R && dx <= R && dy >= -R && dy <= R)
      fillRect(ox + (dx + R) * S, oy + (dy + R) * S, S, S, blink ? rgb(120, 255, 200) : rgb(30, 90, 70));
  }
  // you, and the way you face
  const mx = ox + R * S + 1 + <i32>((px - <f32>cx0 - 0.5) * <f32>S), my = oy + R * S + 1 + <i32>((py - <f32>cy0 - 0.5) * <f32>S);
  fillRect(mx, my, 2, 2, rgb(255, 255, 255));
  fillRect(mx + <i32>(dirX * 4.0), my + <i32>(dirY * 4.0), 1, 1, rgb(255, 255, 120));
}

function drawHud(): void {
  // crystal counter: eight small diamonds that fill in as you collect them
  for (let i = 0; i < NCRYSTALS; i++) {
    const x = 6 + i * 9;
    const got = unchecked(alive[i]) == 0;
    const gem = i & 3;
    const c = got ? rgb(unchecked(GEM_R[gem]), unchecked(GEM_G[gem]), unchecked(GEM_B[gem])) : rgb(40, 48, 70);
    fillRect(x + 3, 4, 1, 1, c);       // a small diamond, five rows tall
    fillRect(x + 2, 5, 3, 1, c);
    fillRect(x + 1, 6, 5, 1, c);
    fillRect(x + 2, 7, 3, 1, c);
    fillRect(x + 3, 8, 1, 1, c);
  }
  // time, as m:ss
  const secs = gameMs / 1000;
  const mm = secs / 60, ss = secs % 60;
  let x = number(mm, 80, 6, rgb(210, 225, 245));
  x = text(":", x, 6, rgb(210, 225, 245));
  number(ss, x, 6, rgb(210, 225, 245), 1, 2);
  minimap();

  if (msgMs > 0) {
    let line1 = "";
    if (msgCode == 1) line1 = "CRYSTAL FOUND";
    else if (msgCode == 2) line1 = "THE EXIT IS OPEN";
    else if (msgCode == 3) line1 = "FIND THE CRYSTALS";
    const w = textWidth(line1, 2);
    const mx = W / 2 - w / 2, my = 24;
    text(line1, mx + 1, my + 1, rgb(0, 0, 0), 2);
    text(line1, mx, my, rgb(255, 255, 255), 2);
  }
  if (collected < NCRYSTALS && state == PLAYING) {
    number(NCRYSTALS - collected, text("LEFT ", 6, H - 11, rgb(190, 205, 230)), H - 11, rgb(255, 255, 255));
  } else if (state == PLAYING) {
    text("REACH THE EXIT", 6, H - 11, rgb(150, 255, 200));
  }
}

function drawTitle(): void {
  dim(0, 40, W, 80, 1);
  const t1 = "CRYSTAL CAVERNS";
  text(t1, W / 2 - textWidth(t1, 3) / 2 + 2, 52, rgb(0, 20, 40), 3);
  text(t1, W / 2 - textWidth(t1, 3) / 2, 50, rgb(150, 235, 255), 3);
  const t2 = "FIND THE CRYSTALS. REACH THE EXIT.";
  text(t2, W / 2 - textWidth(t2) / 2, 76, rgb(220, 232, 248));
  const t3 = "ARROWS MOVE   A B SIDESTEP   POINTER STEERS";
  text(t3, W / 2 - textWidth(t3) / 2, 88, rgb(150, 168, 196));
  if (((clock >> 9) & 1) == 0) {
    const t4 = "PRESS START OR CLICK";
    text(t4, W / 2 - textWidth(t4) / 2, 106, rgb(255, 230, 120));
  }
  if (save[1] > 0) {
    const bt = <i32>save[1] / 1000;
    const x = number(bt / 60, text("BEST ", 6, H - 11, rgb(255, 210, 90)), H - 11, rgb(255, 210, 90));
    number(bt % 60, text(":", x, H - 11, rgb(255, 210, 90)), H - 11, rgb(255, 210, 90), 1, 2);
  }
}

function drawWon(): void {
  dim(0, 38, W, 86, 1);
  const t1 = "YOU ESCAPED!";
  text(t1, W / 2 - textWidth(t1, 3) / 2 + 2, 46, rgb(0, 30, 20), 3);
  text(t1, W / 2 - textWidth(t1, 3) / 2, 44, rgb(150, 255, 200), 3);
  const secs = gameMs / 1000;
  let x = W / 2 - 36;
  x = text("TIME ", x, 74, rgb(210, 225, 245), 2);
  x = number(secs / 60, x, 74, rgb(255, 255, 255), 2);
  x = text(":", x, 74, rgb(255, 255, 255), 2);
  number(secs % 60, x, 74, rgb(255, 255, 255), 2, 2);
  if (newBest) {
    const t2 = "NEW BEST TIME!";
    text(t2, W / 2 - textWidth(t2) / 2, 94, rgb(255, 210, 90));
  } else if (save[1] > 0) {
    const bt = <i32>save[1] / 1000;
    let bx = text("BEST ", W / 2 - 24, 94, rgb(255, 210, 90));
    bx = number(bt / 60, bx, 94, rgb(255, 210, 90));
    bx = text(":", bx, 94, rgb(255, 210, 90));
    number(bt % 60, bx, 94, rgb(255, 210, 90), 1, 2);
  }
  if (((clock >> 9) & 1) == 0) {
    const t4 = "PRESS START FOR A NEW CAVE";
    text(t4, W / 2 - textWidth(t4) / 2, 108, rgb(255, 230, 120));
  }
}

// ---- the frame ----------------------------------------------------------------------------

function tryMove(dx: f32, dy: f32): void {
  const r: f32 = 0.2;
  const nx = px + dx, ny = py + dy;
  // slide along walls: test each axis on its own, leaving a margin around the player
  if (!wallAt(nx + (dx > 0 ? r : -r), py + r) && !wallAt(nx + (dx > 0 ? r : -r), py - r)) px = nx;
  if (!wallAt(px + r, ny + (dy > 0 ? r : -r)) && !wallAt(px - r, ny + (dy > 0 ? r : -r))) py = ny;
}

function updateParticles(dt: i32): void {
  const s = <f32>dt * 0.001;
  for (let i = 0; i < MAXP; i++) {
    if (unchecked(plife[i]) <= 0) continue;
    unchecked((plife[i] -= dt));
    unchecked((pvz[i] -= 1.6 * s));                               // gravity
    unchecked((ppx[i] += pvx[i] * s));
    unchecked((ppy[i] += pvy[i] * s));
    unchecked((ppz[i] += pvz[i] * s));
    if (unchecked(ppz[i]) < 0.02) { unchecked((ppz[i] = 0.02)); unchecked((pvz[i] = -pvz[i] * 0.45)); }   // bounce on the floor
    if (wallAt(unchecked(ppx[i]), unchecked(ppy[i]))) unchecked((plife[i] = 0));
  }
}

function play(dt: i32, buttons: i32, pointerX: i32, pointerY: i32, pointerDown: i32): void {
  gameMs += dt;
  const sec = <f32>dt * 0.001;
  let turn: f32 = 0, walk: f32 = 0, strafe: f32 = 0;
  if (buttons & LEFT) turn -= 1;
  if (buttons & RIGHT) turn += 1;
  if (buttons & UP) walk += 1;
  if (buttons & DOWN) walk -= 1;
  if (buttons & BTN_A) strafe -= 1;
  if (buttons & BTN_B) strafe += 1;
  if (pointerDown != 0) {
    turn += <f32>(pointerX - W / 2) / <f32>(W / 2) * 1.4;
    walk += pointerY < 118 ? 0.8 : pointerY > 142 ? -0.8 : 0;
  }
  ang += turn * 2.1 * sec;
  dirX = Mathf.cos(ang); dirY = Mathf.sin(ang);
  planeX = -dirY * 0.66; planeY = dirX * 0.66;
  tryMove(dirX * walk * 2.6 * sec + planeX / 0.66 * strafe * 2.0 * sec, dirY * walk * 2.6 * sec + planeY / 0.66 * strafe * 2.0 * sec);

  // pick up crystals by walking into them
  for (let i = 0; i < NCRYSTALS; i++) {
    if (unchecked(alive[i]) == 0) continue;
    const dx = <f32>unchecked(crystalX[i]) + 0.5 - px, dy = <f32>unchecked(crystalY[i]) + 0.5 - py;
    if (dx * dx + dy * dy < 0.42) {
      unchecked((alive[i] = 0));
      collected++;
      const gem = i & 3;
      burst(<f32>unchecked(crystalX[i]) + 0.5, <f32>unchecked(crystalY[i]) + 0.5, 0.34,
        unchecked(GEM_R[gem]), unchecked(GEM_G[gem]), unchecked(GEM_B[gem]), 26, 1.6);
      flash = 150;
      if (collected == NCRYSTALS) { portalOpen = true; setMessage(2, 2600); } else setMessage(1, 1100);
    }
  }

  // walk into the open portal to win
  if (portalOpen) {
    const dx = <f32>exitX + 0.5 - px, dy = <f32>exitY + 0.5 - py;
    if (dx * dx + dy * dy < 0.34) {
      state = WON;
      startHeld = true;
      if (save[1] == 0 || <u32>gameMs < save[1]) { save[1] = <u32>gameMs; newBest = true; }
      save[2] += 1;
      burst(px, py, 0.5, 120, 255, 200, 40, 2.4);
    }
  }
}

export function frame(dt: i32, buttons: i32, pointerX: i32, pointerY: i32, pointerDown: i32): void {
  dt = min(max(dt, 1), 80);
  clock += dt;
  flicker = 0.93 + 0.045 * Mathf.sin(<f32>clock * 0.013) + 0.025 * Mathf.sin(<f32>clock * 0.031 + 1.0);
  if (msgMs > 0) msgMs -= dt;
  if (flash > 0) flash = max(0, flash - dt / 3);
  const wantsStart = (buttons & START) != 0 || pointerDown != 0;

  if (state == TITLE) {
    ang += <f32>dt * 0.00035;                                  // the camera idles, turning slowly, behind the title
    if (wantsStart && !startHeld) { state = PLAYING; setMessage(3, 1800); startHeld = true; }
    startHeld = startHeld && wantsStart;
  } else if (state == PLAYING) {
    play(dt, buttons, pointerX, pointerY, pointerDown);
  } else {
    ang += <f32>dt * 0.0006;
    if (((clock >> 7) & 7) == 0) burst(px + dirX * 1.2, py + dirY * 1.2, 0.5, 255, 190, 80, 4, 1.4);
    if (wantsStart && !startHeld) { newCave(); state = PLAYING; setMessage(3, 1800); }
    startHeld = wantsStart;
  }
  if (state != PLAYING) {
    dirX = Mathf.cos(ang); dirY = Mathf.sin(ang);
    planeX = -dirY * 0.66; planeY = dirX * 0.66;
  }
  updateParticles(dt);

  drawFloorAndCeiling();
  drawWalls();
  drawSprites();

  if (flash > 0) {
    const f = flash;
    for (let y = 0; y < H; y += 2) {             // every other row: a scan-line flash at half the cost
      let p = changetype<usize>(fb) + <usize>(y * W * 4);
      for (let x = 0; x < W; x++) { addPixel(p, f >> 1, f >> 1, f >> 1); p += 4; }
    }
  }

  if (state == TITLE) drawTitle();
  else if (state == WON) drawWon();
  else drawHud();
}
