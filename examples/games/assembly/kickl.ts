// Helpers shared by the Kickl example games. Nothing here touches the outside world:
// a Kickl game owns a 240 x 160 picture and arithmetic, and that is all.

export const W: i32 = 240;
export const H: i32 = 160;

/** The picture the browser shows. One u32 per pixel, 0xAABBGGRR (little-endian RGBA). */
export const fb: StaticArray<u32> = new StaticArray<u32>(W * H);

/** The address of the picture, which is what the browser's framebuffer() export must return. */
export function framebufferAddress(): i32 {
  return <i32>changetype<usize>(fb);
}

// Button bit flags passed to frame()
export const LEFT: i32 = 1;
export const RIGHT: i32 = 2;
export const UP: i32 = 4;
export const DOWN: i32 = 8;
export const BTN_A: i32 = 16;
export const BTN_B: i32 = 32;
export const START: i32 = 64;

/** Pack a colour. Alpha is always 255. */
@inline export function rgb(r: i32, g: i32, b: i32): u32 {
  return 0xff000000 | (<u32>(b & 255) << 16) | (<u32>(g & 255) << 8) | <u32>(r & 255);
}

/** Put one pixel; anything off the picture is ignored, so callers never need to clip. */
@inline export function px(x: i32, y: i32, c: u32): void {
  if (<u32>x < <u32>W && <u32>y < <u32>H) unchecked((fb[y * W + x] = c));
}

/** Fill the whole picture. Walking a pointer is several times cheaper than computing y * W + x for every pixel. */
export function clear(c: u32): void {
  let p = changetype<usize>(fb);
  const end = p + <usize>(W * H * 4);
  while (p < end) {
    store<u32>(p, c);
    p += 4;
  }
}

/** Fill one horizontal run of pixels [x0, x1) on row y, clipped to the picture. */
export function hline(y: i32, x0: i32, x1: i32, c: u32): void {
  if (<u32>y >= <u32>H) return;
  x0 = max(x0, 0);
  x1 = min(x1, W);
  let p = changetype<usize>(fb) + <usize>((y * W + x0) * 4);
  const end = p + <usize>((x1 - x0) * 4);
  while (p < end) {
    store<u32>(p, c);
    p += 4;
  }
}

export function fillRect(x: i32, y: i32, w: i32, h: i32, c: u32): void {
  const y0 = max(y, 0), y1 = min(y + h, H);
  for (let yy = y0; yy < y1; yy++) hline(yy, x, x + w, c);
}

/** A tiny, fast, seedable random number generator (xorshift32). The browser gives games no clock and no randomness: use the seed. */
export class Rng {
  s: u32;
  constructor(seed: i32) {
    this.s = seed == 0 ? 0x9e3779b9 : <u32>seed;
    for (let i = 0; i < 4; i++) this.next();
  }
  next(): u32 {
    let x = this.s;
    x ^= x << 13;
    x ^= x >> 17;
    x ^= x << 5;
    this.s = x;
    return x;
  }
  /** 0 <= n < limit */
  below(limit: i32): i32 {
    return <i32>(this.next() % <u32>limit);
  }
  /** 0.0 <= n < 1.0 */
  unit(): f32 {
    return <f32>(this.next() >> 8) * (1.0 / 16777216.0);
  }
}

// ---- a 3x5 pixel font (generated from pixel art; see the tutorial) -------------------------------------------------------------------
// Each glyph is 15 bits, row by row, top to bottom, 3 bits a row (leftmost pixel = highest bit).
const GLYPHS: StaticArray<u16> = [
  0x7B6F, 0x2C97, 0x73E7, 0x73CF, 0x5BC9, 0x79CF, 0x79EF, 0x7252, 0x7BEF, 0x7BCF,
  0x7BED, 0x6BAE, 0x7927, 0x6B6E, 0x79E7, 0x79E4, 0x796F, 0x5BED, 0x7497, 0x126F,
  0x5BAD, 0x4927, 0x5FED, 0x6B6D, 0x7B6F, 0x7BE4, 0x7B79, 0x7BAD, 0x79CF, 0x7492,
  0x5B6F, 0x5B6A, 0x5BFD, 0x5AAD, 0x5A92, 0x72A7, 0x0000, 0x0002, 0x0410, 0x72C2,
  0x01C0, 0x12A4, 0x2482
];

/** Draw one glyph (index into the font table: 0-9 digits, 10-35 letters A-Z, 36 space, 37 ".", 38 ":", 39 "?", 40 "-", 41 "/", 42 "!"). */
export function glyph(g: i32, x: i32, y: i32, c: u32, scale: i32 = 1): void {
  const bits = <i32>unchecked(GLYPHS[g]);
  for (let row = 0; row < 5; row++) {
    for (let col = 0; col < 3; col++) {
      if ((bits >> (14 - row * 3 - col)) & 1) fillRect(x + col * scale, y + row * scale, scale, scale, c);
    }
  }
}

/** Draw text in capitals, digits and a few marks. Returns the x just after the text. */
export function text(s: string, x: i32, y: i32, c: u32, scale: i32 = 1): i32 {
  for (let i = 0; i < s.length; i++) {
    const ch = s.charCodeAt(i);
    let g: i32 = 36; // space
    if (ch >= 48 && ch <= 57) g = ch - 48;
    else if (ch >= 65 && ch <= 90) g = ch - 65 + 10;
    else if (ch >= 97 && ch <= 122) g = ch - 97 + 10;
    else if (ch == 46) g = 37;
    else if (ch == 58) g = 38;
    else if (ch == 63) g = 39;
    else if (ch == 45) g = 40;
    else if (ch == 47) g = 41;
    else if (ch == 33) g = 42;
    glyph(g, x, y, c, scale);
    x += 4 * scale;
  }
  return x;
}

/**
 * Draw a whole number, padded with zeros to at least `digits` digits. Returns the x just after it.
 * Use this instead of ("" + n).toString() inside frame(): a Kickl game has no garbage collector, so every string it builds
 * is memory it never gets back, and a game that builds a string every frame eventually runs out of its memory cap.
 */
export function number(n: i32, x: i32, y: i32, c: u32, scale: i32 = 1, digits: i32 = 1): i32 {
  if (n < 0) { glyph(40, x, y, c, scale); x += 4 * scale; n = -n; }
  let divisor: i32 = 1;
  let count: i32 = 1;
  while (n / divisor >= 10) { divisor *= 10; count++; }
  for (let pad = count; pad < digits; pad++) { glyph(0, x, y, c, scale); x += 4 * scale; }
  while (divisor > 0) {
    glyph((n / divisor) % 10, x, y, c, scale);
    x += 4 * scale;
    divisor /= 10;
  }
  return x;
}

/** Width in pixels of a string drawn with text(). */
export function textWidth(s: string, scale: i32 = 1): i32 {
  return s.length * 4 * scale - scale;
}
