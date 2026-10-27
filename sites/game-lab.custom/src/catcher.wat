;; Catcher — a sample game for the Kickl sandbox (ABI kickl-game-1).
;;
;; Move the green bar with the left/right keys (or drag the pointer) and catch the falling block.
;; Each catch scores a point and speeds the block up; a miss resets the score. The best score is the
;; game's save data, which the browser keeps encrypted in the reader's own profile.
;;
;; The game imports nothing. The host gives it no clock, no randomness, no network and no files:
;; the seed arrives in init(), and everything else is arithmetic on its own memory.
;;
;; Build:  wat2wasm catcher.wat -o ../blobs/catcher.wasm      (WABT)
;;   or:   python -c "import wasmtime;open('../blobs/catcher.wasm','wb').write(wasmtime.wat2wasm(open('catcher.wat').read()))"
;;
;; Memory map (4 pages = 262,144 bytes):
;;   0        framebuffer, 240x160 RGBA8 (153,600 bytes)
;;   160000   player x       160004 block x      160008 block y
;;   160012   score          160016 rng state
;;   160100   best score (save area, 4 bytes) — restored by the host before init()
(module
  (memory (export "memory") 4)

  ;; 0xAABBGGRR as little-endian RGBA
  (global $BG      i32 (i32.const 0xff301810))
  (global $YELLOW  i32 (i32.const 0xff40d0ff))
  (global $GREEN   i32 (i32.const 0xff80e040))
  (global $WHITE   i32 (i32.const 0xffffffff))
  (global $CYAN    i32 (i32.const 0xffe0c040))

  (func $rand (result i32)
    (local $s i32)
    (local.set $s
      (i32.add (i32.mul (i32.load (i32.const 160016)) (i32.const 1664525)) (i32.const 1013904223)))
    (i32.store (i32.const 160016) (local.get $s))
    (i32.and (i32.shr_u (local.get $s) (i32.const 16)) (i32.const 32767)))

  ;; fill a clipped rectangle
  (func $fill (param $x i32) (param $y i32) (param $w i32) (param $h i32) (param $c i32)
    (local $i i32) (local $j i32) (local $x1 i32) (local $y1 i32)
    (if (i32.lt_s (local.get $x) (i32.const 0))
      (then (local.set $w (i32.add (local.get $w) (local.get $x))) (local.set $x (i32.const 0))))
    (if (i32.lt_s (local.get $y) (i32.const 0))
      (then (local.set $h (i32.add (local.get $h) (local.get $y))) (local.set $y (i32.const 0))))
    (local.set $x1 (i32.add (local.get $x) (local.get $w)))
    (local.set $y1 (i32.add (local.get $y) (local.get $h)))
    (if (i32.gt_s (local.get $x1) (i32.const 240)) (then (local.set $x1 (i32.const 240))))
    (if (i32.gt_s (local.get $y1) (i32.const 160)) (then (local.set $y1 (i32.const 160))))
    (local.set $j (local.get $y))
    (block $done
      (loop $rows
        (br_if $done (i32.ge_s (local.get $j) (local.get $y1)))
        (local.set $i (local.get $x))
        (block $rowdone
          (loop $cols
            (br_if $rowdone (i32.ge_s (local.get $i) (local.get $x1)))
            (i32.store
              (i32.mul (i32.add (i32.mul (local.get $j) (i32.const 240)) (local.get $i)) (i32.const 4))
              (local.get $c))
            (local.set $i (i32.add (local.get $i) (i32.const 1)))
            (br $cols)))
        (local.set $j (i32.add (local.get $j) (i32.const 1)))
        (br $rows))))

  (func $min (param $a i32) (param $b i32) (result i32)
    (select (local.get $a) (local.get $b) (i32.lt_s (local.get $a) (local.get $b))))

  (func (export "kickl_abi") (result i32) (i32.const 1))
  (func (export "framebuffer") (result i32) (i32.const 0))
  (func (export "save_ptr") (result i32) (i32.const 160100))
  (func (export "save_len") (result i32) (i32.const 4))

  (func (export "init") (param $seed i32)
    (i32.store (i32.const 160016) (i32.or (local.get $seed) (i32.const 1)))
    (i32.store (i32.const 160000) (i32.const 108))
    (i32.store (i32.const 160004) (i32.rem_u (call $rand) (i32.const 232)))
    (i32.store (i32.const 160008) (i32.const 0))
    (i32.store (i32.const 160012) (i32.const 0)))

  (func (export "frame")
        (param $dt i32) (param $buttons i32) (param $ptrx i32) (param $ptry i32) (param $down i32)
    (local $px i32) (local $fx i32) (local $fy i32) (local $score i32) (local $best i32)
    (local.set $px    (i32.load (i32.const 160000)))
    (local.set $fx    (i32.load (i32.const 160004)))
    (local.set $fy    (i32.load (i32.const 160008)))
    (local.set $score (i32.load (i32.const 160012)))
    (local.set $best  (i32.load (i32.const 160100)))

    ;; input: left = 1, right = 2, or follow the pointer while it is down
    (if (i32.and (local.get $buttons) (i32.const 1))
      (then (local.set $px (i32.sub (local.get $px) (i32.const 3)))))
    (if (i32.and (local.get $buttons) (i32.const 2))
      (then (local.set $px (i32.add (local.get $px) (i32.const 3)))))
    (if (local.get $down)
      (then (local.set $px (i32.sub (local.get $ptrx) (i32.const 12)))))
    (if (i32.lt_s (local.get $px) (i32.const 0)) (then (local.set $px (i32.const 0))))
    (if (i32.gt_s (local.get $px) (i32.const 216)) (then (local.set $px (i32.const 216))))

    ;; the block falls faster as the score climbs
    (local.set $fy
      (i32.add (local.get $fy)
        (i32.add (i32.const 2) (call $min (i32.div_u (local.get $score) (i32.const 5)) (i32.const 4)))))

    (if (i32.ge_s (local.get $fy) (i32.const 136))
      (then
        (if (i32.and
              (i32.gt_s (i32.add (local.get $fx) (i32.const 8)) (local.get $px))
              (i32.lt_s (local.get $fx) (i32.add (local.get $px) (i32.const 24))))
          (then
            (local.set $score (i32.add (local.get $score) (i32.const 1)))
            (if (i32.gt_u (local.get $score) (local.get $best))
              (then (local.set $best (local.get $score)))))
          (else (local.set $score (i32.const 0))))
        (local.set $fy (i32.const 0))
        (local.set $fx (i32.rem_u (call $rand) (i32.const 232)))))

    (i32.store (i32.const 160000) (local.get $px))
    (i32.store (i32.const 160004) (local.get $fx))
    (i32.store (i32.const 160008) (local.get $fy))
    (i32.store (i32.const 160012) (local.get $score))
    (i32.store (i32.const 160100) (local.get $best))

    ;; draw
    (call $fill (i32.const 0) (i32.const 0) (i32.const 240) (i32.const 160) (global.get $BG))
    (call $fill (i32.const 4) (i32.const 4)
      (call $min (i32.mul (local.get $score) (i32.const 4)) (i32.const 232)) (i32.const 4) (global.get $WHITE))
    (call $fill (i32.const 4) (i32.const 10)
      (call $min (i32.mul (local.get $best) (i32.const 4)) (i32.const 232)) (i32.const 2) (global.get $CYAN))
    (call $fill (local.get $fx) (local.get $fy) (i32.const 8) (i32.const 8) (global.get $YELLOW))
    (call $fill (local.get $px) (i32.const 144) (i32.const 24) (i32.const 8) (global.get $GREEN)))
)
