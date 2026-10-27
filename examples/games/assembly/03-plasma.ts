// Step 3: what the instruction budget buys. A full-screen "plasma" effect: every one of the 38,400 pixels is a sum of four
// waves looked up in tables. A (A) toggles the palette; moving the pointer pushes the waves around.
// Build it, run `kickl game-check`, and see how much of the 10,000,000-instruction frame budget a full-screen effect uses.
import { W, H, fb, rgb, text, BTN_A, framebufferAddress } from "./kickl";

export function kickl_abi(): i32 { return 1; }
export function framebuffer(): i32 { return framebufferAddress(); }

// Work is moved out of frame() and into init(), which has a bigger budget and runs once: tables, not maths, per pixel.
const sinTab = new StaticArray<u8>(256);          // one full sine wave scaled to 0..255
const palettes = new StaticArray<u32>(256 * 3);   // three 256-colour palettes
const dist = new StaticArray<u8>(W * H);          // distance of every pixel from the centre, scaled

let t: i32 = 0;           // the animation clock, in milliseconds
let palette: i32 = 0;
let aHeld: bool = false;
let shiftX: i32 = 0;
let shiftY: i32 = 0;

/** base + amp * sin(angle), as a whole number. AssemblyScript will not mix whole numbers and fractions in one sum, so say what you mean. */
function wave(angle: f32, base: f32, amp: f32): i32 {
  return <i32>(base + amp * Mathf.sin(angle));
}

export function init(seed: i32): void {
  for (let i = 0; i < 256; i++) {
    unchecked((sinTab[i] = <u8>(127.5 + 127.5 * Mathf.sin(<f32>i * (Mathf.PI * 2.0 / 256.0)))));
  }
  for (let i = 0; i < 256; i++) {
    const a = <f32>i * (Mathf.PI * 2.0 / 256.0);
    // 0: hot sunset   1: deep sea   2: neon
    unchecked((palettes[i] = rgb(wave(a, 127.0, 127.0), wave(a * 2.0 + 1.0, 60.0, 60.0), wave(a + 1.5707964, 90.0, -80.0))));
    unchecked((palettes[256 + i] = rgb(wave(a, 20.0, 30.0), wave(a + 1.5, 90.0, 100.0), wave(a + 3.0, 150.0, 100.0))));
    unchecked((palettes[512 + i] = rgb(wave(a * 3.0, 128.0, 127.0), wave(a * 3.0 + 2.1, 128.0, 127.0), wave(a * 3.0 + 4.2, 128.0, 127.0))));
  }
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const dx = <f32>(x - W / 2), dy = <f32>(y - H / 2);
      unchecked((dist[y * W + x] = <u8>min<f32>(255.0, Mathf.sqrt(dx * dx + dy * dy) * 2.2)));
    }
  }
}

export function frame(dt: i32, buttons: i32, pointerX: i32, pointerY: i32, pointerDown: i32): void {
  t += dt;
  const a = (buttons & BTN_A) != 0;
  if (a && !aHeld) palette = (palette + 1) % 3;
  aHeld = a;
  if (pointerDown != 0) { shiftX = pointerX; shiftY = pointerY; }

  const t1 = t >> 4, t2 = (t * 3) >> 6, t3 = t >> 5;
  const pal = changetype<usize>(palettes) + <usize>(palette * 256 * 4);
  const sin = changetype<usize>(sinTab);
  const d = changetype<usize>(dist);
  let out = changetype<usize>(fb);

  // The hot loop. Every operation here runs 38,400 times a frame, so it is written with a walking pointer and table lookups
  // instead of y * W + x and sin().
  for (let y = 0; y < H; y++) {
    const yy = y + shiftY;
    const rowWave = <i32>load<u8>(sin + ((yy * 3 + t2) & 255));
    for (let x = 0; x < W; x++) {
      const v = <i32>load<u8>(sin + ((x * 2 + t1 + shiftX) & 255))
              + rowWave
              + <i32>load<u8>(sin + (((x + yy) * 2 + t3) & 255))
              + <i32>load<u8>(sin + ((<i32>load<u8>(d + y * W + x) + t1) & 255));
      store<u32>(out, load<u32>(pal + ((v >> 2) << 2)));
      out += 4;
    }
  }

  text("PLASMA", 5, 5, rgb(255, 255, 255));
  text("A: PALETTE  POINTER: PUSH", 5, H - 10, rgb(255, 255, 255));
}
