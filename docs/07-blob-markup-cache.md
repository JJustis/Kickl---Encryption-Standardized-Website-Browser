# 07 — KML Markup, Blobs, and the Steganographic Vault

## 1. Goals

1. A sealed document is **structure + ciphertext**, never a script.
2. Every content block is individually sealed; the reader chooses what to open.
3. Saved pages are **images you can re-read offline**, with only the blobs you opened visible.
4. Nothing in the pipeline can reach the network, execute code, or exfiltrate by side channel.

## 2. KML markup (v1)

KML is a small, strict XML dialect. It is parsed with a validating parser; an unknown element or
attribute is **rejected** (not silently dropped) so authors learn the boundary.

### 2.1 Elements

| Element | Children | Attributes | Purpose |
|---------|----------|-----------|---------|
| `kml` | `head`, `body` | `v="1"`, `site`, `epoch` | root |
| `head` | `meta`, `title`, `styles` | — | metadata |
| `meta` | — | `name`, `content` | `description`, `author`, `created`, `updated`, `ttl` |
| `title` | text | — | document title |
| `styles` | text (OSS) | — | scoped stylesheet |
| `body` | blocks | — | content |
| `page` | blocks | `width` | vertical flow container |
| `stack` | blocks | `gap` | vertical stack |
| `row` | blocks | `gap`, `align` | horizontal row |
| `grid` | blocks | `cols`, `gap` | grid |
| `card` | blocks | `tone`, `pad` | surface |
| `h1`..`h4` | text | `tone` | headings |
| `p` | inline | `tone`, `align` | paragraph |
| `text` | inline | `tone`, `mono`, `size` | generic text |
| `a` | inline | `href` (kickl/mailto/#) | link |
| `code` | text | `lang` | code block |
| `quote` | blocks | `cite` | quotation |
| `hr` | — | — | divider |
| `img` | — | `src` (data: or `kickl-blob:`), `alt`, `w`, `h` | inline image |
| `blob` | — | `id`, `ref`, `mime`, `w`, `h`, `label` | **sealed block** |
| `file` | — | `id`, `ref`, `name`, `size`, `mime` | sealed attachment |
| `button` | text | `intent` (`mail`, `chat`, `copy`, `vault`) | no script; maps to a native action |
| `badge` | text | `tone` | small status pill |
| `html` / `visual` | text (HTML fragment) | — | sanitized HTML with inline SVG; no CSS engine |
| `ad` | — | `publisher`, `site`, `space`, `token` | opt-in NCKL publisher slot; native, no script |

**Inline** elements: `b`, `i`, `u`, `s`, `code`, `a`, `span`, `br`, `badge`, `text`.

Forbidden by design: `script`, `iframe`, `object`, `embed`, `form`, `input`, `video`, `audio`,
`canvas`, `math`, external stylesheets, `style` attributes on HTML, `on*` attributes,
`meta http-equiv`, `<base>`, and any remote URL. Inline `<svg>` is allowed **inside** the sanitized
`html` block; it is static drawing, not a script or navigation surface. This is the authoring choice:
a div filled with inline SVG instead of a CSS dialect.

The HTML subset is intentionally small: containers (`div`, `center`, `section`, `article`, `header`,
`footer`), text (`p`, `h1`–`h4`, `b`, `i`, `u`, `s`, `code`, `pre`, `quote`), lists, simple tables,
`hr`, `br`, raster images, and inline SVG (shapes, paths, gradients, text, `use`, internal `#id`
references). Links are inert text; forms and event handlers are removed. There is no CSS engine, no
layout escape from the page surface, and no network loader.

The inline parser bounds input to 96 KiB, 8,192 nodes, and 48 levels of nesting. The renderer
sanitizes the fragment again immediately before parsing it for native controls, so callers cannot
bypass the publication sanitizer. `SealedSurfacePolicy` centralizes the `jttp` top-level scheme,
JavaScript-disabled, no-network, and local-subresource rules. Its CSP value is intended for a future
WebView adapter; it is not represented as browser-enforced CSP in the current native renderer.

An `<ad>` block is created only when the webmaster pasted the official NCKL publisher snippet into
the inline HTML. Publishing verifies the site-bound token and replaces the loader with this block.
The loader's `nickles.js` is a manifest, never executed; the browser resolves the signed creative
locally and labels it `AD`. A copied token or an edited space fails closed. RSS stories cannot carry
an `<ad>` block.

**New custom names start from an example.** When a `jttp://` name is not in the phonebook, the
browser opens an editor pre-filled with a complete inline SVG/HTML page for that host, plus a live
preview. The author learns the format by modifying a working page.

**RSS is not a free page.** `jttp://name.rss` follows its fixed story template: image, headline,
author, date, location, story, tags. It rejects the `html`/`visual` field. Inline HTML/SVG is for
`.com` and other custom names. An RSS story is authored as XML instead:

```xml
<rss version="2.0"><channel><item>
  <title>Headline</title>
  <author>unnamed</author>
  <pubDate>2026-10-24</pubDate>
  <location>The mesh</location>
  <description><![CDATA[## The story

Text only.]]></description>
  <category>rss</category>
  <category>story</category>
  <enclosure url="C:\images\story.png" type="image/png"/>
</item></channel></rss>
```

The `<item>` is parsed into the fixed fields; the optional `<enclosure>` path becomes the sealed
image attachment. Nested inline HTML/SVG in the XML is treated as story text, never rendered as a
visual block.

**Sealed images inside inline SVG.** An `<image href="kickl-blob:ID">` or `<img src="kickl-blob:ID">`
is a public reference to a blob in the same sealed package. At publish time the composer extracts
every raster `data:image/...` URL out of the sanitized fragment, rewrites the attribute to
`kickl-blob:<content-hash-id>`, and seals the bytes with document-key AES-256-GCM. The KML therefore
contains no base64 image and no plaintext image at rest. Until the reader clicks **UNLOCK SEALED
IMAGE**, the renderer substitutes a 1×1 transparent placeholder and draws the unlock control beside
the SVG. On unlock the browser decrypts the blob into the tab, re-renders, and zeroes the bytes on
lock, navigation, tab close, or window close.

```html
<!-- author writes this -->
<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180">
  <image href="data:image/png;base64,…" width="320" height="180"/>
</svg>

<!-- sealed KML carries this -->
<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180">
  <image href="kickl-blob:v9f2c1a4d77b0e31" width="320" height="180"/>
</svg>
```

### 2.2 Example

```xml
<kml v="1" site="nyx.custom" epoch="2026-10-21T09:00:00Z">
  <head>
    <title>Field notes — /research/2026-10</title>
    <meta name="author" content="kb1q9x7…"/>
    <meta name="ttl" content="2592000"/>
    <styles><![CDATA[
      @tokens { ink: #E8EEF6; dim: #6C7C8E; acc: #5CFF9D; gap: 12px; }
      p      { color: var(--ink); font-size: 15px; line-height: 1.55; }
      .meta  { color: var(--dim); font-family: mono; font-size: 11px; letter-spacing: .08em; }
      .card  { background: #101720; border: 1px solid #1E2833; radius: 10px; pad: 16px; }
    ]]></styles>
  </head>
  <body>
    <page width="760">
      <card>
        <h1 tone="acc">Observation 14</h1>
        <p class="meta">2026-10-21 · sealed</p>
        <p>Two of the three relays rotated within the same epoch. Opened with <code>pin</code>.</p>
        <blob id="b1" ref="sha256:9f2c…" mime="text/html" label="Transcript (4 KiB)" w="720" h="180"/>
        <blob id="b2" ref="sha256:1a7e…" mime="image/png" label="Diagram (48 KiB)" w="720" h="420"/>
        <file id="f1" ref="sha256:77ab…" name="runbook.pdf" size="812344" mime="application/pdf"/>
        <button intent="mail">Reply to author</button>
      </card>
    </page>
  </body>
</kml>
```

### 2.3 OSS — the style language

OSS/KSS is a *subset* of CSS with tokens, compiled to a single scoped stylesheet. It is not "CSS with
extra powers"; it is CSS with fewer. The current browser-first slice does **not** require KSS: authors
can use the `html` block with inline SVG instead. KSS remains a planned convenience layer, not a
prerequisite for a sealed visual page.

```ebnf
stylesheet  = { tokens | rule } ;
tokens      = "@tokens" "{" { ident ":" value ";" } "}" ;
rule        = selector "{" { decl } "}" ;
selector    = type [ "." class ] { "," type [ "." class ] } ;   (* no combinators, no pseudo *)
decl        = property ":" value ";" ;
property    = "color" | "background" | "font-family" | "font-size" | "font-weight"
            | "letter-spacing" | "line-height" | "text-align" | "margin" | "padding"
            | "gap" | "border" | "radius" | "width" | "height" | "max-width"
            | "display" | "grid-cols" | "opacity" ;
value       = number unit | hexcolor | ident | "var(" "--" ident ")" ;
unit        = "px" | "%" | "em" | "rem" | "fr" ;
```

Rules:
- Selectors: type and class only, no `#id`, no descendant, no attribute, no `:hover` (hover is applied
  by the renderer's own stylesheet), no `@media` in v1.
- No `url()` **except** `url(data:image/...)` with a hard 256 KiB cap and a MIME allowlist.
- No `position`, no `z-index`, no `overflow`, no `transform`, no `animation`, no `filter`. Layout is
  owned by the renderer so a document cannot overlay the chrome or spoof a dialog.
- `font-family`: only `ui` and `mono` tokens.
- Max stylesheet 64 KiB, max 400 rules, max 64 tokens.
- Unknown property or value → compile error shown to the author, document rejected.

This is deliberately restrictive: a sealed document must not be able to phish with a fake toolbar,
cover the panic button, or exfiltrate via CSS.

## 3. Document envelope

```
KML-DOC = CBOR {
  v: 1,
  site: "nyx.custom",
  path: "/research/2026-10",
  version: 7,                        // record version the doc was signed for
  created: 1792800000,
  ttl: 2592000,
  owner: "kb1…",
  markup: bytes,                     // KML XML (UTF-8, ≤ 512 KiB)
  blobs: [ { id, ref, mime, size, key: <wrapped DEK>, w, h } ],
  sig: bytes                         // hybrid signature over the canonical map minus sig
}
```

- The document is delivered over the sealed channel (HPKE to the reader + owner signature), so the
  markup itself is private in transit.
- `ref` is the BLAKE3-256 of the blob **plaintext**, giving content addressing and dedupe.
- Blob keys are wrapped to the reader with HPKE; the same blob can be shared to several readers
  without re-encrypting the payload (multi-recipient wrap, `docs/03` §4).
- `ttl` is advisory; the client warns before opening a stale document.

## 4. Blob lifecycle

```
sealed ──click──► PIN gate ──► unwrap key ──► AEAD open ──► sanitize ──► render
   │                                                                       │
   └───────────── "unopened" state persists in the vault ──────────────────┘
```

- A blob is `sealed` until the user clicks it. No background decryption, no prefetch of blob
  plaintext, no "open all" default (an explicit "open all (N)" exists behind a confirm).
- Decrypted fragments live in a session cache (`SecureBuffer`, 5-min idle TTL, max 32 MiB) keyed by
  `(site, ref, epoch)`. Cache is memory-only; it is cleared on lock, tab close, and panic.
- Each unlock writes an audit line (local, encrypted): `{time, site, blobId, result}` — never content.
- Failed tag / wrong key → the blob stays sealed, shows `could not open`, and increments a local
  integrity counter. Repeated failures on a saved document show "this copy may be damaged or
  tampered with".
- Blob plaintext types allowed: `text/html` (sanitized to inline subset), `text/plain`, `image/png`,
  `image/jpeg`, `application/pdf` (opened in the OS viewer only after an explicit confirm, or in the
  clearnet modal if a PDF engine is available), `application/octet-stream` (save only).
- Size caps: inline render ≤ 2 MiB; attachment ≤ 25 MiB; anything larger requires a streaming open and
  a progress UI.

### 4.1 Sanitization pipeline (defence in depth)

1. **No script engine.** The sealed surface sets `IsScriptEnabled=false`; CSP also says
   `script-src 'none'`.
2. **Parse** the inline fragment with HtmlAgilityPack; comments are dropped and malformed markup is
   repaired into a canonical fragment before it enters the page model.
3. **Allowlist** elements/attributes; remove scripts, stylesheet elements, forms, frames, objects,
   event handlers, `style` attributes on HTML, external references, and `foreignObject`. Only
   `data:image/png|jpeg|gif|webp` images and internal `#id` SVG references survive. Inline SVG element
   and attribute names are canonicalized before rendering.
4. **CSS**: no raw CSS is accepted in the current slice. SVG presentation attributes and a small
   allowlist of SVG `style` properties (paint, stroke, opacity, font) are permitted; `@import`,
   external `url()`, and `expression()` are rejected.
5. **CSP** injected by the custom-scheme handler:
   `default-src 'none'; img-src data:; style-src 'nonce-<r>'; font-src 'none'; connect-src 'none';
    frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`.
6. **Network kill switch**: the sealed profile's `WebResourceRequested` handler blocks every request
   whose scheme is not `jttp` or `data`; `NavigationStarting` cancels anything else.
7. **Iframe sandbox** (`sandbox=""`) for the one place raw HTML may appear: an explicitly "open as
   HTML" preview of an untrusted fragment, with a warning.

**Test corpus:** ≥ 500 payloads (mXSS, SVG, MathML, `srcdoc`, CSS `@import`, `url()` exfiltration,
`meta refresh`, unicode confusables, huge attribute bombs, entity expansion, billion laughs). CI must
show zero execution and zero outbound requests.

## 5. Per-blob visibility is a first-class state

The document model tracks, per blob: `sealed | opened | failed`. The vault persists that map.

- Saving a page with 12 blobs and 3 opened stores: background image with 9 regions redacted, 3
  fragments encrypted under the vault key, and the 9 original ciphertexts (so they can still be opened
  later **if the user still has a key wrap** — otherwise they remain sealed forever, which is the
  documented behaviour).
- "Only opened blobs visible" is therefore enforced at *render* time, not by deleting data.
- The vault viewer shows a counter: `3 / 12 opened · 9 sealed`.

## 6. The vault container (`.kickl.png`)

A real PNG that any viewer opens, with the payload in a private ancillary chunk.

```
┌ PNG signature (8) ──────────────────────────────────────────────┐
│ IHDR  ── thumbnail dimensions (≤ 640 px long edge)              │
│ IDAT  ── coarse, blurred thumbnail (unencrypted, low detail)     │
│ tEXt  ── "Software: Kickl Browser 1.0" (optional, innocuous)      │
│ kiKl  ── KML container v1 (encrypted payload)                  │
│ IEND                                                            │
└─────────────────────────────────────────────────────────────────┘
```

Chunk type `kiKl`: ancillary (`o`), private (`m`), reserved-uppercase (`N`), unsafe-to-copy (`i`).
Private ancillary chunks are ignored by conforming decoders, so the file stays a valid PNG.

### 6.1 `kiKl` payload

```
offset size field
0      4    "OMN1"
4      1    container version (1)
5      1    flags (bit0 = payload split, bit1 = manifest in LSB)
6      2    chunk index (0-based)
8      2    chunk count
10     8    total payload length
18     16   blob id (BLAKE3 truncated)
34     12   AES-256-GCM nonce
46     32   AAD hash (BLAKE3 of header fields 0..45)
78     n    ciphertext (AES-256-GCM, 128-bit tag appended)
```

- AAD = `site || epoch || counter || totalLen || chunkIndex || chunkCount`. Reordering or splicing
  chunks fails authentication.
- Max container 64 MiB; a 12 MP full-page screenshot compresses to ~1–4 MiB as PNG, so one chunk is
  typical; split when > 8 MiB to keep individual chunks friendly to tools.
- Key = `HKDF(vaultMaster, info="kickl/v1/vault", salt=blobId||counter)`; ratchet as in `docs/03` §5.3.
- Plaintext payload = CBOR:

```
{
  v: 1, site, path, epoch, savedAt,
  image: { w, h, format: "png", bytes },      // full-page screenshot, unopened regions redacted
  blobs: [ { id, ref, rect:{x,y,w,h}, state, mime, ct?, fragment? } ],
  docHash, sig, owner
}
```

- `fragment` = decrypted, sanitized HTML for opened blobs, re-encrypted under the vault key.
- `ct` = original ciphertext for sealed blobs.
- Integrity: BLAKE3 of the whole plaintext stored in the header; AEAD tag covers it.

### 6.2 Thumbnail honesty

The thumbnail is a **privacy trade-off**. Default: 320 px long edge, Gaussian blur σ=8, saturation
reduced, text unreadable. The settings page offers: `blurred` (default), `solid` (no thumbnail), or
`sharp` (warned). The vault grid can always show a generated abstract gradient instead.

### 6.3 Optional LSB steganography

- Payload hidden in the thumbnail's RGB LSBs: capacity = `floor(W*H*3/8)` bytes.
  320×200 → 24,000 bytes. Enough for the **manifest only** (site id, hashes, blob states ≈ 1–3 KiB),
  not the page.
- Why optional: LSB does not survive re-encoding, resizing, or screenshots; it is obfuscation, not
  protection. It is useful when a file is casually inspected by a person, not by a forensic tool.
- Embedding: 3 LSBs per pixel (1 per channel), sequential with a keyed permutation
  (`HKDF-SHA-256(vault, salt=seed, info="kickl/v1/stego-permutation")` determines pixel order;
  the envelope is AES-256-GCM under a sibling key). Extraction requires the vault/session key.
  If the bitstream is corrupt, fall back to the `kiKl` chunk.
- The security of the file is the AEAD; **never** the stego. State this in the UI.
- The slice uses the same scheme with the session secret and a lossless PNG carrier; JPEG is not
  used because its lossy transform destroys LSB data (see `docs/18`).

### 6.4 Capacity and performance facts

| Item | Number |
|------|--------|
| Full-page PNG, 1440×9000, mostly text | 1.5–4 MB |
| AES-256-GCM throughput (desktop, AES-NI) | ~2–6 GB/s |
| Vault write (4 MB) | ~15–40 ms + fsync |
| LSB capacity, 320×200 thumbnail | 24 KB |
| LSB capacity, 1920×1080 frame | 777 KB |
| Max page pixels captured | 64 MP (tiles beyond) |
| Max vault item | 64 MiB |

## 7. Capturing and refreshing

### 7.1 Capture

- Use WebView2 `CallDevToolsProtocolMethodAsync("Page.captureScreenshot", { format:"png",
  captureBeyondViewport:true, clip })` with `Page.getLayoutMetrics` for full height.
- Cap at 64 MP / 16384 px per edge; longer pages are tiled and stored as multiple images with
  offsets (the viewer stitches).
- Capture **after** the redaction overlay is applied, so unopened blobs are baked out.
- Store the DOM-free image + blob rects; never store the raw DOM (it would leak sealed text).
- Exclude the dock and any chrome by capturing only the page surface.

### 7.2 Refresh from the owner

- The URL bar's `⟳` (or the viewer's "Refresh from owner") performs a fresh signed fetch:
  `jttp://site/path` over the sealed channel, verifying the owner signature and record version.
- Diff by `ref` (content hash):
  - unchanged blobs keep their opened state and fragments;
  - changed blobs reset to `sealed` and are marked `updated`;
  - removed blobs show `no longer published` (kept locally until the user deletes them);
  - new blobs appear sealed.
- If the owner key changed, the refresh is blocked behind the key-change interstitial
  (`docs/05` §4) unless the rotation is signed by the old key.
- If offline, the button is disabled with "owner unreachable — showing saved copy (age 4 d)".

## 8. Performance and limits

| Operation | Target |
|-----------|--------|
| Parse + sanitize 100 KiB document | < 40 ms |
| Render skeleton (20 blobs) | < 400 ms |
| Unlock one 64 KiB text blob | < 250 ms incl. PIN cache |
| Save vault item (4 MB) | < 600 ms |
| Open vault item (decrypt + decode) | < 500 ms |
| Vault grid scroll (200 items) | 60 fps |

Hard limits: document ≤ 512 KiB markup, ≤ 200 blobs, ≤ 64 MiB total blobs, ≤ 25 MiB attachment,
≤ 64 KiB stylesheet, ≤ 96 KiB inline HTML/SVG fragment, nesting depth ≤ 32, attribute count ≤
32/element, text node ≤ 256 KiB.

## 9. Threat notes

| Threat | Control |
|--------|---------|
| Malicious markup | strict KML parser + HTML/SVG allowlist + no script + CSP |
| Inline SVG payload (script, foreignObject, external refs) | sanitizer removes executable elements, event handlers, external `href`/`url()`, and nested SVG data URLs |
| CSS exfiltration (`url()`, `@import`) | current slice accepts no HTML CSS; SVG `style` is filtered and CSP is `default-src 'none'` |
| Overlay phishing in-page | no `position`/`z-index`/`transform`; chrome is native and always on top |
| Decompression bomb (PNG) | WIC decode limits (pixel cap, memory cap), reject on exceed |
| Huge/looping document | element/attr/text caps, parse timeout, cancellation |
| Blob substitution | `ref` hash verified after decrypt; AAD binds site/epoch/blob id |
| Replay of an old document | version + signature + `notAfter`; UI shows age and stale warning |
| Vault theft | AEAD + per-blob keys + TPM-wrapped vault key; crypto-erase on panic |
| Thumbnail leak | blur by default, option to disable, documented |
| Stale cached page mistaken for live | persistent "saved copy" banner with age and a one-click refresh |

## 10. Implementation checklist (2026-10-25)

- [x] Sealed document blobs and explicit per-blob unlock exist in the browser; opened bytes are
  cleared when the session locks.
- [x] PNG carrier and steganographic cache primitives exist in `Kickl.Core.Stego`.
- [ ] Page capture, redaction pipeline, thumbnail generation, and saved-copy refresh UI.
- [ ] `.kickl.png` vault create/import/export/delete flows and searchable vault gallery.
- [ ] Vault ratchet/key-wrap lifecycle, per-blob visibility persistence, and crash-safe writes.
- [ ] Supported-platform key wrapping and forensic wipe verification.

The existing per-page unlock/cache and PNG primitives are not a complete vault product.
