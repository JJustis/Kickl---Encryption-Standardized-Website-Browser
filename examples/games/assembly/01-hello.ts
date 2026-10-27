// Step 1: the smallest Kickl game. It paints a colour gradient and a bouncing square.
import { W, H, rgb, hline, fillRect, framebufferAddress } from "./kickl";

let t: i32 = 0; // milliseconds since the game started: the only "clock" a game has

// ---- the five exports every Kickl game must provide -----------------------------------
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
