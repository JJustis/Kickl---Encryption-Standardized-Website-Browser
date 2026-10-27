// The cave: a maze made from the seed, with some loops knocked through so it is not just one long corridor.
import { Rng } from "./kickl";

export const MW: i32 = 25;            // 12 x 12 rooms: each room is one open square on an odd grid position
export const MH: i32 = 25;
export const NCRYSTALS: i32 = 8;

/** 0 = open floor, 1..3 = a wall (stone, mossy stone, rune stone). */
export const map = new StaticArray<u8>(MW * MH);

export let startX: i32 = 1;
export let startY: i32 = 1;
export let exitX: i32 = 1;
export let exitY: i32 = 1;
export const crystalX = new StaticArray<i32>(NCRYSTALS);
export const crystalY = new StaticArray<i32>(NCRYSTALS);

const dist = new StaticArray<i32>(MW * MH);
const stack = new StaticArray<i32>(MW * MH);
const queue = new StaticArray<i32>(MW * MH);

@inline export function cell(x: i32, y: i32): i32 {
  return <u32>x < <u32>MW && <u32>y < <u32>MH ? <i32>unchecked(map[y * MW + x]) : 1;
}

function span(x: i32): i32 { return x < 0 ? -x : x; }

function wallType(rng: Rng): u8 {
  const r = rng.below(100);
  return r < 62 ? 1 : r < 88 ? 2 : 3;      // mostly stone, some moss, a few glowing runes
}

export function generate(rng: Rng): void {
  for (let i = 0; i < MW * MH; i++) unchecked((map[i] = wallType(rng)));

  // Depth-first "recursive backtracker": walk to an unvisited room two squares away, knocking out the wall between
  let top = 0;
  unchecked((map[1 * MW + 1] = 0));
  unchecked((stack[top++] = 1 * MW + 1));
  const dx: StaticArray<i32> = [2, -2, 0, 0];
  const dy: StaticArray<i32> = [0, 0, 2, -2];
  while (top > 0) {
    const cur = unchecked(stack[top - 1]);
    const cx = cur % MW, cy = cur / MW;
    let options = 0;
    let pick = -1;
    for (let d = 0; d < 4; d++) {
      const nx = cx + unchecked(dx[d]), ny = cy + unchecked(dy[d]);
      if (nx > 0 && nx < MW - 1 && ny > 0 && ny < MH - 1 && unchecked(map[ny * MW + nx]) != 0) {
        options++;
        if (rng.below(options) == 0) pick = d;       // choose one of the options evenly
      }
    }
    if (pick < 0) { top--; continue; }
    const nx = cx + unchecked(dx[pick]), ny = cy + unchecked(dy[pick]);
    unchecked((map[(cy + (ny - cy) / 2) * MW + cx + (nx - cx) / 2] = 0));
    unchecked((map[ny * MW + nx] = 0));
    unchecked((stack[top++] = ny * MW + nx));
  }

  // Knock a few walls through so there are loops to get lost in
  for (let k = 0; k < 22; k++) {
    const x = 1 + rng.below(MW - 2), y = 1 + rng.below(MH - 2);
    if (((x ^ y) & 1) == 1 && cell(x, y) != 0 && cell(x - 1, y) + cell(x + 1, y) + cell(x, y - 1) + cell(x, y + 1) <= 2) {
      unchecked((map[y * MW + x] = 0));
    }
  }

  // How far is every open square from the start, walking? Breadth-first.
  for (let i = 0; i < MW * MH; i++) unchecked((dist[i] = -1));
  let head = 0, tail = 0;
  startX = 1; startY = 1;
  unchecked((dist[startY * MW + startX] = 0));
  unchecked((queue[tail++] = startY * MW + startX));
  const bx: StaticArray<i32> = [1, -1, 0, 0];
  const by: StaticArray<i32> = [0, 0, 1, -1];
  let far = 0;
  exitX = startX; exitY = startY;
  while (head < tail) {
    const cur = unchecked(queue[head++]);
    const x = cur % MW, y = cur / MW;
    const d = unchecked(dist[cur]);
    if (d > far && (x & 1) == 1 && (y & 1) == 1) { far = d; exitX = x; exitY = y; }   // the exit is the farthest room
    for (let k = 0; k < 4; k++) {
      const nx = x + unchecked(bx[k]), ny = y + unchecked(by[k]);
      if (cell(nx, ny) == 0 && unchecked(dist[ny * MW + nx]) < 0) {
        unchecked((dist[ny * MW + nx] = d + 1));
        unchecked((queue[tail++] = ny * MW + nx));
      }
    }
  }

  // Scatter the crystals: far from the start, from the exit and from each other (relaxing the spacing if the cave is cramped)
  let placed = 0;
  for (let spacing = 8; spacing >= 3 && placed < NCRYSTALS; spacing -= 2) {
    for (let attempt = 0; attempt < 1500 && placed < NCRYSTALS; attempt++) {
      const x = 1 + 2 * rng.below(MW / 2), y = 1 + 2 * rng.below(MH / 2);
      if (cell(x, y) != 0 || unchecked(dist[y * MW + x]) < 6) continue;
      if (span(x - exitX) + span(y - exitY) < spacing) continue;
      let ok = true;
      for (let i = 0; i < placed; i++) {
        if (span(x - unchecked(crystalX[i])) + span(y - unchecked(crystalY[i])) < spacing) { ok = false; break; }
      }
      if (!ok) continue;
      unchecked((crystalX[placed] = x));
      unchecked((crystalY[placed] = y));
      placed++;
    }
  }
  // If the cave is somehow too small, put the rest in the start room's neighbours rather than leave them undefined
  while (placed < NCRYSTALS) {
    unchecked((crystalX[placed] = exitX));
    unchecked((crystalY[placed] = exitY));
    placed++;
  }
}
