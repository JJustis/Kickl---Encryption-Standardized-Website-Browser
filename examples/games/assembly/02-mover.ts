// Step 2: input, a seeded random number generator, text, and a save that survives between visits.
// Collect as many coins as you can in 20 seconds. Arrow keys or the pointer move you; Start restarts.
import { W, H, rgb, hline, fillRect, text, number, textWidth, Rng, LEFT, RIGHT, UP, DOWN, START, framebufferAddress } from "./kickl";

// ---- the exports the browser looks for ---------------------------------------------------
export function kickl_abi(): i32 { return 1; }
export function framebuffer(): i32 { return framebufferAddress(); }

// A save area: the browser copies the reader's previous save in BEFORE init() and reads it back every few seconds.
// Four bytes are enough here: the best score, little-endian.
const save = new StaticArray<u8>(4);
export function save_ptr(): i32 { return <i32>changetype<usize>(save); }
export function save_len(): i32 { return 4; }

const ROUND_MS: i32 = 20000;
const NCOINS: i32 = 4;

let rng: Rng = new Rng(1);
let px: f32 = 120, py: f32 = 80;      // the player's centre
let cx: StaticArray<i32> = new StaticArray<i32>(NCOINS);
let cy: StaticArray<i32> = new StaticArray<i32>(NCOINS);
let score: i32 = 0;
let best: i32 = 0;
let left: i32 = ROUND_MS;             // milliseconds left in the round
let startHeld: bool = true;           // so a held button does not restart the round at once
let clock: i32 = 0;

function bestScore(): i32 {
  return <i32>load<u32>(changetype<usize>(save));
}

function placeCoin(i: i32): void {
  unchecked((cx[i] = 12 + rng.below(W - 24)));
  unchecked((cy[i] = 22 + rng.below(H - 34)));
}

function newRound(): void {
  score = 0;
  left = ROUND_MS;
  px = 120;
  py = 90;
  for (let i = 0; i < NCOINS; i++) placeCoin(i);
}

export function init(seed: i32): void {
  rng = new Rng(seed);   // the seed is the only randomness a game gets; the same seed gives the same game
  best = bestScore();
  newRound();
}

export function frame(dt: i32, buttons: i32, pointerX: i32, pointerY: i32, pointerDown: i32): void {
  clock += dt;
  const wantsStart = (buttons & START) != 0 || pointerDown != 0;

  if (left > 0) {
    left -= dt;
    // Movement is speed x time, so the game runs the same whatever the frame rate
    const step = <f32>dt * 0.09;
    let mx: f32 = 0, my: f32 = 0;
    if (buttons & LEFT) mx -= 1;
    if (buttons & RIGHT) mx += 1;
    if (buttons & UP) my -= 1;
    if (buttons & DOWN) my += 1;
    if (pointerDown != 0) {           // or walk towards the pointer
      const dx = <f32>pointerX - px, dy = <f32>pointerY - py;
      const d = Mathf.sqrt(dx * dx + dy * dy);
      if (d > 3) { mx = dx / d; my = dy / d; }
    }
    px = max<f32>(5, min<f32>(px + mx * step, <f32>(W - 5)));
    py = max<f32>(21, min<f32>(py + my * step, <f32>(H - 5)));

    for (let i = 0; i < NCOINS; i++) {
      if (abs(<f32>unchecked(cx[i]) - px) < 8 && abs(<f32>unchecked(cy[i]) - py) < 8) {
        score++;
        placeCoin(i);
        if (score > best) { best = score; store<u32>(changetype<usize>(save), <u32>best); }   // saved by the browser
      }
    }
    if (left <= 0) { left = 0; startHeld = wantsStart; }
  } else {
    if (wantsStart && !startHeld) { newRound(); }
    startHeld = wantsStart;
  }

  // ---- drawing -------------------------------------------------------------------------
  for (let y = 0; y < H; y++) hline(y, 0, W, rgb(14 + y / 12, 18 + y / 9, 40 + y / 5));
  fillRect(0, 0, W, 16, rgb(8, 10, 22));
  // No string building here: number() draws digits straight onto the picture, so nothing is allocated per frame
  number(score, text("SCORE ", 6, 5, rgb(255, 255, 255)), 5, rgb(255, 255, 255));
  number(best, text("BEST ", 90, 5, rgb(255, 210, 90)), 5, rgb(255, 210, 90));
  const secs = (left + 999) / 1000;
  const timeColor = secs <= 5 ? rgb(255, 110, 110) : rgb(150, 220, 255);
  number(secs, text("TIME ", W - 52, 5, timeColor), 5, timeColor);

  const sparkle = (clock / 120) & 3;
  for (let i = 0; i < NCOINS; i++) {
    const x = unchecked(cx[i]), y = unchecked(cy[i]);
    fillRect(x - 3, y - 3, 7, 7, rgb(255, 200, 40));
    fillRect(x - 2, y - 2, 3, 3, rgb(255, 245, 160));
    if (sparkle == i) fillRect(x - 5, y - 1, 11, 3, rgb(255, 240, 180));
  }

  const ix = <i32>px, iy = <i32>py;
  fillRect(ix - 4, iy - 4, 9, 9, rgb(110, 235, 190));
  fillRect(ix - 2, iy - 2, 2, 2, rgb(10, 30, 30));
  fillRect(ix + 1, iy - 2, 2, 2, rgb(10, 30, 30));

  if (left <= 0) {
    fillRect(40, 60, 160, 44, rgb(8, 10, 22));
    text("TIME UP", W / 2 - textWidth("TIME UP", 2) / 2, 66, rgb(255, 255, 255), 2);
    text("PRESS START OR CLICK", W / 2 - textWidth("PRESS START OR CLICK") / 2, 90, rgb(150, 220, 255));
  }
}
