# Changelog

## 0.8.0 — final Lite release before the mobile pivot

This is the last planned feature release for the WebAssembly Lite
build. Heuristic DLA on PDF.js's word-level extraction has a ceiling:
multi-column papers with tight figure layouts keep finding edge cases
that an unmodelled segmenter can't reliably solve. The structurally
right answer for a polished commercial rail-reader is a native build
that can carry an actual layout model — see
`RailDLA/PORTING.md` for the model + algorithmic stack the next
codebase will follow (.NET MAUI + ONNX Runtime + PP-DocLayoutV3 is
the planned target).

Lite stays public, MIT, and reusable. It remains a working
WebAssembly PDF viewer with drag-to-copy, search, manual zoom,
outline, click-to-snap, free pan, and a usable-on-most-text-pages
rail mode. The rail-mode work — RailDLA-derived algorithmic ports,
column-aware reading order, decoration filtering, the overlay
interactions — are the load-bearing pieces and they transfer to
mobile unchanged because they operate on
`RailReader.Core` abstractions.

**RailDLA ports + rail-mode bug fixes.** The v0.7.0 PDF.js bring-up
banked the speed; this release fixes the rail-mode quality issues
the user surfaced (reading order broken on multi-column papers,
line highlight covering multiple lines, headers/footers walked as
nav targets, app hanging when switching PDFs, ←/→ should scroll
horizontally not advance lines, scroll-anchor too jittery).

The three algorithmic ports come from
[RailDLA](https://github.com/sjvrensburg/RailDLA) — the Python
prototype where these have been validated against a 44-PDF / 126-page
academic corpus. RailDLA itself ports them from PdfPig, which
implements the underlying literature (O'Gorman 1993, Klampfl 2014).

### Added

- **Klampfl reading order via Allen's interval algebra**
  (`KlampflReadingOrder.cs`). Replaces the v0.7.0 path that used
  `XYCutPlusPlusResolver` from Core. Each block projects to X and
  Y intervals; two blocks have a "before in reading" relation when
  their interval-pair relations match one of a small set of
  column-wise patterns. The relations form a directed graph;
  topological order falls out by repeatedly removing the node with
  the most outgoing edges. Per RailDLA/PORTING.md §3.5, ~26% of
  pages in the validation corpus disagreed with naive lattice
  sorts; Klampfl was correct on every audited case.
- **Histogram-peak Docstrum** (`DocstrumSegmenter.cs`). Replaces
  v0.7.0's fixed-threshold word-level clusterer. Estimates the
  within-line and between-line gaps from the most-populated bucket
  of the nearest-neighbour distance histogram (O'Gorman 1993,
  axis-aligned bounding-box-input variant). Block construction
  requires ≥10% X-overlap between consecutive lines — that's the
  geometric path to column awareness with no raster needed.
- **Klampfl decoration classifier** (`KlampflDecoration.cs`).
  Cross-page text repetition for running headers, footers, and
  page numbers. Levenshtein edit distance over digit-normalised
  text × geometric IoU. The `MaxDecorationLen = 500` length
  fast-path is preserved verbatim — without it, body-vs-body
  Levenshtein would dominate runtime. Duplex handling: for docs
  with >3 pages, compare p ↔ p±2 (not p±1) so alternating
  headers on left/right pages still match. Rail-nav skips
  decoration-flagged blocks.
- **Per-line bounding rects** in the segmentation output. The
  active-line overlay now hugs each line's actual extent (Left,
  Right, Top, Bottom from Docstrum) instead of the block's
  full width.

### Changed

- **Anchored-cursor scroll.** Instead of `BringIntoView` (which
  snaps the active line to "just visible" and lets it drift around
  the viewport as you advance), the View now computes the scroll
  offset that places the active line at a fixed fraction (1/3)
  of viewport height. The page scrolls smoothly underneath while
  the reading position stays put — much closer to how the desktop
  rail-reader feels.
- **`←` / `→` propagate to the `ScrollViewer`** so they scroll
  horizontally (the v0.6.0 behaviour you originally asked for).
  v0.6.x had them as line-nav per a request that was later
  reversed; this restores the horizontal-pan semantic. Line nav
  stays on `↑` / `↓`; Home / End jump to first/last line of page.

### Fixed

- **Switching to a new PDF no longer hangs the app.** A monotonic
  generation counter is bumped before the new doc opens; every
  async analysis / text-extract helper captures the generation
  at start and bails (without writing to caches) if the generation
  changed while it was awaiting JS interop. Pending tasks from the
  old doc no longer write into the new doc's cache and wedge it.

### Performance

- Docstrum's histogram-peak estimate self-adapts to a document's
  actual word spacing — tight academic typesetting and loose
  body-copy work with the same code, no per-document tuning.
- The decoration classifier runs once per document on the
  background thread, after at least two pages are analysed.
  Idempotent and pre-warmed.

### Rail-mode polish (desktop-alignment iteration)

After the first v0.8.0 manual-test pass, four refinements to align
the rail experience with the desktop app:

- **`←` / `→` horizontal page-scroll** — programmatically driven
  via `ScrollViewer.Offset.X` (the events don't reach the
  ScrollViewer when the focusable UserControl absorbs arrow keys).
  Step is 80 px per press. Works at any zoom.
- **Click-to-snap-to-line.** Pointer release within ~4 px of press
  is treated as a click rather than a drag — the rail cursor
  snaps to the block whose bbox contains the point, and the line
  whose Y-centre is nearest. Drag still does drag-to-copy.
- **Cubic ease-out scroll** replaces the instant `Offset` set
  from v0.8.0's first iteration. ~180 ms animation; rapid
  consecutive presses cancel the in-flight animation so motion
  stays continuous rather than queuing.
- **Free pan via `Ctrl+drag`** — Ctrl held at pointer-down enters
  a temporary pan mode where the cursor delta drags the page.
  Rail overlay hides during pan. On pointer release, the rail
  cursor re-snaps to the line nearest the viewport centre.
- **Line focus dim mask** — four 40%-opacity black rectangles
  cover the area outside the active line band, so the eye
  anchors on the bright line without per-frame raster work.
  Rendered as Canvas overlays; zero cost beyond the four rect
  layouts.

### Docstrum hardening (after initial 0.8.0 manual testing)

The first 0.8.0 pass landed Klampfl + Docstrum but real PDFs
surfaced two failure modes that needed dedicated fixes:

- **Per-character text emission breaks histogram-peak.** Some
  scientific PDFs emit text per-glyph (kerning) rather than per-
  word; the histogram-peak gap estimate then locks onto the ~0 pt
  intra-word spacing and the line clusterer never joins anything.
  Fix: floor the within-line gap at 30 % of median item height
  (and the between-line gap at 80 %). On normal word-level
  extraction the estimate dominates; the floor only kicks in for
  the broken-emission case.
- **Justified text can collapse Docstrum's word-gap estimate above
  the column-gutter width.** Loose justification produces 10–15 pt
  inter-word gaps; with the 3× multiplier `maxWithin` reaches
  30–45 pt, which is wider than a typical academic column gutter,
  so the line clusterer unions a paragraph end with the figure
  caption next to it. Fix: hard cap `maxWithin` at 2.5 % of page
  width (≈ 15 pt for a US-letter page) — smaller than every
  gutter we've seen in real corpora.
- **Stray narrow blocks distort the column-wise reading order.**
  Klampfl's "strictly left of" predicate ignores Y, so a 6 pt-wide
  single-character block sorts ahead of any wider block it
  precedes horizontally — regardless of vertical position. Fix:
  drop blocks below `BlockMinAreaFrac = 0.0006` of page area,
  ported from RailDLA. On a 612 × 792 page this prunes anything
  smaller than ~290 pt² (citation superscripts, decoration
  glyphs, footnote markers).

### Diagnostic logging

Per-page DLA emits a one-liner with the actual gap thresholds
(`[Docstrum] pageW=… maxWithin=… maxBetween=…`) plus a block-by-
block dump (raw, then reordered). Useful when revisiting Lite
later or porting the algorithmic stack to the mobile build.

## 0.7.0

**Backend swap.** v0.6.0 worked but was structurally too slow:
PdfPig (pure-managed parsing) + SkiaSharp (software rasterisation),
both on the .NET WASM single thread, can't compete with the native
PDF viewers users compare against. This release swaps the entire
parse + render path to PDF.js, which runs in a Web Worker (free
background thread) and uses the browser's GPU-accelerated Canvas2D.
All the rail-mode UI, the XY-Cut reading-order resolver, and the
LayoutBlock / LineInfo types from `RailReader.Core` transfer
unchanged — they were designed against the Core abstractions, not
the PDF backend.

### Added

- **PDF.js 5.7.284 bundled into `wwwroot/lib/pdfjs/`.** Modern ES
  module build (`pdf.min.mjs` + `pdf.worker.min.mjs`). Loaded by a
  side-effect import in `main.js`.
- **`pdfjs-shim.mjs`** — thin JS wrapper that exposes
  `globalThis.RailReaderPdfJs` with five async methods
  (`openDoc` / `closeDoc` / `getPageSize` / `renderPage` /
  `getTextItemsJson` / `getOutlineJson`). Returns JSON strings for
  small structured data and a raw `Uint8Array` for the render
  payload. Maintains a per-document handle cache + a per-page text
  cache.
- **`IPdfJsRuntime` + `PdfJsRuntimeRegistry`** (shared project) —
  abstraction layer so the platform-agnostic VM doesn't pull in
  `[JSImport]` (which only compiles against the `browser` TFM). The
  Browser entry point constructs a concrete
  `PdfJsRuntime` and publishes it via the registry on startup.
- **`PdfJsRuntime`** (Browser project) — `[JSImport]`-backed
  implementation. Splits `renderPage` into an async `Task` + sync
  `byte[]` fetch + two sync int getters; the JSImport source
  generator can't marshal `Task<byte[]>` (SYSLIB1072), but supports
  the pieces separately. Safe because renders are serialised in the
  VM.
- **`PdfJsSession`** (shared project) — per-document session that
  owns a PDF.js document handle. Exposes async methods for page
  size, render, text, blocks, outline. Built on top of
  `IPdfJsRuntime` so it's testable without a browser.
- **Word-level Docstrum-equivalent DLA.** PDF.js's `getTextContent`
  emits word-level rects (each item is typically a word). The new
  clusterer in `PdfJsSession` runs two phases: items → lines
  (mid-Y cluster + horizontal-gap split — this is what catches
  column gutters), then lines → blocks (column-aware vertical
  clustering that requires X-range overlap before extending an
  existing block). Reading order still comes from
  `XYCutPlusPlusResolver` in Core.

### Changed

- **`MainViewModel`** rewritten end-to-end against `PdfJsSession`.
  All session calls are async; analysis runs without `Task.Run`
  because PDF.js already provides background work via the Web
  Worker. Text extraction is sync-cached + async-pre-fetched so
  drag-to-copy (which needs a sync read inside the user-gesture
  frame) still works without a stall.
- **Bitmap pipeline.** PDF.js returns RGBA from
  `ImageData.data`, so the buffer is mutated in place for both
  search-hit blending and the final RGBA → BGRA swizzle into the
  Avalonia `WriteableBitmap`. One byte[] allocation instead of two.
- **Outline conversion** at the VM boundary: PDF.js's resolved
  page indices → Core's `OutlineEntry.Page` (nullable).

### Removed

- `RailReader.Core.PdfPig` direct dependency (was transitive; now
  not needed at all).
- `RailReader.Renderer.PdfPigSkia` package — no longer imported.
- `LitePdfPigSession` — superseded by `PdfJsSession`.
- Two PdfPig-flavoured smoke tests (the packages they exercised
  are gone).

### Build dependency

`wasm-tools` workload is still required for the SkiaSharp native
link (Avalonia uses SkiaSharp for text rendering even though Lite
no longer rasterises PDFs through it). Same install instructions
as v0.3.1's CHANGELOG.

## 0.6.0

Rail mode lands — the headline feature for the v0.x series. The page
is segmented into blocks via PdfPig's real
`DocstrumBoundingBoxes` (word-aware, column-friendly), reading order
is assigned by `XYCutPlusPlusResolver` (from Core), and each block
already carries its `TextLines` from PdfPig — no inline char-cluster
detection needed. When the user zooms past 1.4× the arrow keys
lock onto lines.

### Added

- **`LitePdfPigSession`.** A per-document session that owns a cached
  `PdfPig.PdfDocument` and exposes (a) per-page text + char boxes
  for search and drag-to-copy, (b) per-page Docstrum blocks for rail
  mode. Replaces the previous design where
  `RailReader.Core.PdfPig.PdfTextService` re-opened the document on
  every call — which was the main source of v1's sluggishness — and
  the charbox-only Docstrum approximation in Core, which lacked the
  word-aware horizontal-gap detection needed to separate columns.
- **Background analysis pipeline.** `EnsurePageAnalysisAsync` runs
  the per-page word extraction + Docstrum on `Task.Run`; concurrent
  callers for the same page share one in-flight task. The result is
  inserted into the cache on the UI thread (async-continuation
  default), and property-changed events fire so the View lights up
  rail mode. Pre-warmed on zoom-crosses-threshold and page-nav at
  high zoom so the user's first ↓/↑ is usually instant.
- **Rail-mode UI loop.** ↑ / ↓ / ← / → all advance line-by-line in
  reading order (cascade across blocks/pages on bounds); Home / End
  jump to first/last line of the current page. Status bar shows
  `Block i/N · Line j/M` while rail mode is active. Block-scoped
  commands (`RailNextBlock` / `RailPrevBlock` / `RailFirstLineOfBlock`
  / `RailLastLineOfBlock`) live on the VM unbound — pencilled in for
  future "skip section" gestures.
- **Smart rail entry.** On the first rail-mode keystroke, the VM
  picks the block + line nearest the *current viewport top* rather
  than block[0] line[0]. Lets the user zoom into the middle of a
  page, hit ↓, and pick up reading from where they are instead of
  snapping to the top of the page.
- **Render coalescing.** Holding `Ctrl+=` no longer queues a separate
  full re-render per keystroke — concurrent render requests collapse
  to one in-flight render with a re-run after if the zoom changed
  during the work. Pretty noticeable on dense academic pages where a
  single render at 3× takes a few hundred ms in WASM.

- **Active block + active line overlay.** Drawn on the same Canvas
  that already hosts the drag-selection rect — a translucent yellow
  fill for the active line, a 2-px blue outline for the active
  block. Auto-scroll via `BringIntoView` keeps the active line in
  the viewport as you advance.

### Changed

- **`MaxZoom` 3.0 → 4.0** (6400 px longest edge, ~85 MB peak RGB
  buffer for a square page). Picked because 3× felt capped on
  half-page figures in two-column papers. If WASM memory pressure
  becomes a problem we can tune down.
- Direct reference to `RailReader.Core.PdfPig` dropped — it's still
  available transitively via the renderer; Lite no longer uses
  `PdfTextService` directly because the session reuses one cached
  `PdfDocument` for everything. Core family stays at 0.7.3 — the
  0.8.0 Docstrum analyzer we drafted in `feat/docstrum-analyzer` on
  the Core repo ended up unused after we pivoted to PdfPig's
  word-aware DLA inside the session.

### Caveats

- Docstrum classifies every block as `BlockRole.Text` — figures,
  tables, equations, headings, footnotes all collapse into one role.
  Rail mode therefore steps through *all* visual regions as if they
  were paragraphs. Fine for text-heavy academic PDFs (the Lite
  sweet spot); painful on layout-heavy documents. An ONNX-classified
  analyzer would do better but doesn't fit Lite's WASM weight budget.
- The active-line overlay clips strictly to the line's extracted
  bounding box. On justified text with tall ascenders/descenders it
  can look snug; we'll tune the height padding once we've used it
  on real documents for a while.
- v1 jumps the viewport to the active line via `BringIntoView` — no
  cubic ease-out snap like the desktop app. That's intentional for
  the MVP; an animated transition is a future PR.

### Tests

- New VM test: rail mode is off in the empty state and stays off when
  zoom is pushed past the threshold without a document loaded;
  rail-nav commands are correctly disabled. Total **9 / 9** pass.

### Performance caveat

Honest assessment: PdfPig + SkiaSharp in single-threaded WASM is
structurally slow compared to native PDF viewers — parsing is
pure-managed C# with no JIT in the browser, and rasterisation is
software-only with no GPU compositing. This release squeezes what
it can out of that stack (cached `PdfDocument`, background analysis,
render coalescing), but rail mode at 3–4× zoom on dense academic
pages still feels sluggish next to the desktop app. v0.7.0 swaps
the rendering + parsing backend to PDF.js (which runs in a Web
Worker and uses hardware-accelerated Canvas2D) to fix this
structurally.

## 0.5.4

The first half of the rail-mode prerequisite work. Manual zoom only —
the actual rail-mode loop (block-lock, line-by-line navigation, active
block overlay) lands in the follow-up release once `RailReader.Core`
ships the lightweight Docstrum analyzer.

### Added

- **Manual zoom (1.0×–3.0×).** Toolbar `−` / `＋` / `Fit` buttons,
  keyboard shortcuts `Ctrl+=`, `Ctrl+−`, `Ctrl+0`, and `Ctrl+Wheel`
  over the page image. Zoom percentage is shown between the buttons.
  Cap at 3.0× (~4800 px longest edge, ~36 MB peak RGB buffer) to keep
  WASM RAM use bounded.
- **Adaptive `Stretch` mode.** The `Image` binds its `Stretch` and
  `StretchDirection` to the VM so it switches between two modes:
  - `Zoom == 1.0`: `Stretch=Uniform` `StretchDirection=DownOnly` —
    the existing fit-to-window behaviour (bitmap shrinks to viewport
    when larger).
  - `Zoom > 1.0`: `Stretch=None` — bitmap displayed at natural pixel
    size, the surrounding `ScrollViewer` scrolls. Page is rendered
    at `BaseRenderTargetSize × Zoom` so glyphs stay sharp.

  Known v1 visual glitch: the displayed page size jumps when
  crossing the 1.0 → 1.25 boundary (fit → natural-pixel). A future
  PR can smooth this by computing an explicit display size from the
  viewport bounds, but it needs viewport feedback from the View into
  the VM and isn't on the rail-mode critical path.

### Tests

- Two new VM tests: zoom defaults to 1.0 and commands gate on
  `HasDocument`; Zoom clamps to [1.0, 3.0] and flips
  `ImageStretch` above 1.0. Total **8 / 8** pass.

## 0.5.3

### Added

- **Live selection highlight during drag-to-copy.** As you drag across
  text on the page, a translucent blue rectangle now tracks the
  pointer in real time so you can see exactly what will land on the
  clipboard. Implemented as a `Canvas` sibling of the page `Image`
  inside the same `Grid` cell, so they share a coordinate system —
  the overlay updates by setting `Canvas.Left/Top` + `Width/Height`
  on a single `Rectangle`, never touches the rendered bitmap, and
  costs effectively nothing per pointer-move event. The deferred
  feature called out in the 0.5.0 CHANGELOG ("would burn 50–100 ms
  re-rendering the bitmap per pointer-move"). The drag anchor is now
  stored in image-local pixel space instead of page-point space so the
  Move handler doesn't have to re-run the inverse mapping on every
  frame; the page-point conversion happens once on release before
  calling `vm.GetSelectedText`.

## 0.5.2

### Fixed

- **Drag-to-copy still produced word-glued text on academic PDFs after
  0.5.1.** The 0.7.2 Core fix used a geometry-based threshold that
  worked on the SkiaSharp test fixture but mis-fired on Frontiers-style
  justified typesetting. 0.7.3 in Core switched to PdfPig's purpose-
  built `NearestNeighbourWordExtractor`; this release picks it up.
  Multi-word search now matches across word boundaries; drag-to-copy
  yields readable text.

## 0.5.1

Two bugs surfaced by trying to actually use the 0.5.0 viewer.

### Fixed

- **Bottom status bar restored.** When the search controls landed in
  the toolbar in 0.5.0, the `StatusText` `TextBlock` got dropped from
  the layout — so "Copied N characters", "Failed to open: …", and the
  current file name all updated in the viewmodel but nothing rendered
  them. Added a dedicated status bar at the bottom of the window.
- **Bumped `RailReader.Core` family → 0.7.2.** That release fixed the
  PdfPig text-extraction bug where word spaces and line breaks weren't
  reconstructed from the geometry (PdfPig.Letters has no explicit
  space tokens). Before 0.7.2, drag-to-copy produced strings like
  `"Besides,eachtypeofmethodisconstracks…"` and any multi-word search
  silently missed every hit. With 0.7.2 the extracted text contains
  word spaces and `'\n'`s at the right places; the existing search /
  selection paths in this app just work.

## 0.5.0

Full-text search and drag-to-copy selection — the two features the
v0.4.0 viewer was missing to be genuinely usable for reading PDFs in
the browser.

### Added

- **Full-text search across the open document.** Toolbar search box
  (right side). Pressing Enter scans every page, highlights all
  matches in soft yellow, navigates to the first match's page, and
  highlights the current match in a brighter yellow. `↑` and `↓`
  buttons cycle through matches across pages; status text reads
  `N / M` to show position. `Esc` (in the search box) clears.
  Case-insensitive substring match. Page text is cached after first
  extraction so subsequent searches/selections don't re-parse.
- **Drag-to-copy selection.** Pointer-press, drag, release on the
  rendered page. Glyphs whose midpoints fall in the dragged rect are
  extracted in reading order and copied to the system clipboard via
  the Avalonia 12 `IDataTransfer` API. Status line confirms
  "Copied N characters." Works the same in the WASM target — the
  browser clipboard API is what backs `IClipboard` there. No live
  highlight during drag in v0.5.0 (would burn 50–100 ms re-rendering
  the bitmap per pointer-move); a future PR can add a Canvas overlay
  for that.

### Rendering

- Search/selection highlights are painted directly into the per-page
  render buffer (semi-transparent yellow rect blend over the RGB
  bytes) before the bitmap is handed to Avalonia. Keeps coordinate
  conversion in one place — page-points → bitmap pixels by
  multiplying by `RenderScale = RenderTargetSize / max(pageW, pageH)`.
  The View only knows about image-local pointer coords; the VM owns
  the rest of the mapping.

### Tests

- New test gates `SearchCommand` on `HasDocument + non-empty query`
  and verifies match navigation commands are disabled in the empty
  state. Total **6 / 6** pass.

## 0.4.0

Two viewer ergonomics features that the v0.3.x MVP was missing, plus
the perf win from picking up RailReaderCore 0.7.1.

### Added

- **Outline panel.** A collapsible left-hand `TreeView` shows the PDF's
  bookmark tree. Clicking an entry navigates to its page. The "Outline"
  toolbar button is only visible when the open document has bookmarks
  and toggles the panel; the panel auto-opens on document load when
  bookmarks exist. Wired through PdfPig's `TryGetBookmarks` (already
  populated on `IPdfService.Outline`).
- **Fit-to-window page rendering.** The `Image` now uses
  `Stretch="Uniform"` with `StretchDirection="DownOnly"`. Large pages
  scale down to fit the viewport (no horizontal scroll on letter-size
  pages in a 1200-px window) while never being upscaled beyond their
  natural render resolution. Render target bumped 1200 → 1600 px
  longest-edge so the down-scaled view stays sharp on retina viewports.

### Changed

- Bumped all family packages 0.7.0 → 0.7.1 to pick up the
  `RailReader.Renderer.PdfPigSkia` perf patch (cached `PdfDocument`,
  byte[] ctor, `IDisposable`).
- **Drop the temp-file hop.** `MainViewModel.OpenAsync` now feeds the
  picker bytes straight into `new PdfPigSkiaPdfService(byte[])`. The
  previous v0.3.1 flow wrote bytes to `Path.GetTempPath()` first
  because Core only exposed a file-path constructor; that overload now
  exists in 0.7.1 and Lite uses it.
- **Dispose the previous document on Open.** When the user opens a
  second PDF in the same session, `MainViewModel` now `Dispose()`s the
  prior `PdfPigSkiaPdfService` to release the cached `PdfDocument`
  deterministically.

### Tests

- 1 new viewmodel test: outline-toggle command flips visibility. Total
  4 → 5 / 5 pass.

## 0.3.1

### Fixed

- **WASM bundle missing native Skia.** Without
  `<WasmBuildNative>true</WasmBuildNative>` in the Browser csproj, the
  build emitted a warning ("`@(NativeFileReference) ... won't be linked
  in`") and produced a WASM bundle that threw
  `System.DllNotFoundException: libSkiaSharp at SKImageInfo..cctor()`
  on the first frame — Avalonia uses SkiaSharp for text rendering, so
  the app showed only the static splash and the Avalonia bootstrap
  never completed. Enabled native linking; first build now compiles
  `libSkiaSharp.a` + `libHarfBuzzSharp.a` into `dotnet.native.wasm`,
  and the viewer renders pages end-to-end in the browser.

### Build dependency

Native linking requires the `wasm-tools` workload (Emscripten 3.1.56
toolchain). Install with `dotnet workload install wasm-tools`. The
existing `.github/workflows/build.yml` already runs this step on CI;
local dev needs it too. On Ubuntu specifically the apt-installed
`dotnet-sdk-10` package mishandles `dotnet workload install` (it
claims success but doesn't persist the workload manifest); install
dotnet user-level via `dotnet-install.sh --channel 10.0 --install-dir
~/.dotnet` and put that on PATH ahead of the system one.

## 0.3.0

First working prototype: open a PDF, see pages, navigate. Wires up the
full RailReaderCore stack — `RailReader.Renderer.PdfPigSkia` 0.7.0 for
rasterisation alongside `RailReader.Core.PdfPig` 0.7.0 for parsing.

### Added

- **File-picker open flow.** Toolbar "Open PDF…" button uses Avalonia's
  `IStorageProvider.OpenFilePickerAsync` (works the same in WASM and on
  desktop hosts). Picked bytes are written to a process-local temp file
  before being handed to `PdfPigSkiaPdfServiceFactory.CreatePdfService`
  — the current Core API takes a file path; a future `byte[]` overload
  on `PdfPigSkiaPdfService` will let us drop this hop.
- **Page-by-page rendering.** `IPdfService.RenderPagePixmap(pageIndex,
  targetSize: 1200)` returns RGB bytes; `RgbToAvaloniaBitmap` packs
  them into an Avalonia `WriteableBitmap` with full-alpha BGRA layout
  via `Marshal.Copy` — no unsafe blocks, portable across desktop and
  WASM. Rendering happens on a background `Task.Run` to keep the UI
  responsive; the result is dispatched back to the UI thread.
- **Navigation toolbar.** Prev / Next buttons (gated by `CanPrev` /
  `CanNext`), live page label "N / Total", busy indicator while a page
  renders or a document loads.
- **Empty state.** Until a document is loaded, the canvas shows
  "RailReaderLite — Open a PDF to begin." Once loaded, the empty state
  hides and the rendered page appears inside a scroll viewer.

### Changed

- `Directory.Packages.props` — bumped all family packages to 0.7.0
  (Core, Core.PdfPig, Renderer.PdfPigSkia).
- `MainViewModel` rewritten end-to-end as a document controller. Replaces
  the version-only smoke shape from 0.2.0.
- `MainView.axaml` rewritten as a real PDF-viewer chrome (toolbar +
  scrollable rendered page) instead of the version-banner placeholder.
- Smoke tests updated to match the new VM surface — 4 assertions all
  pass (empty-state shape; Core, Core.PdfPig, Renderer.PdfPigSkia all
  consumable from the shared net10.0 lib half).

### Known limitations / honest caveats

- **First open is slow.** PdfPig parses the document on every render
  call (`PdfDocument.Open` is per-call inside the gate); for a multi-
  page PDF this means re-parsing per page. Mitigation: future PR caches
  page sizes or moves to a longer-lived `PdfDocument` instance.
- **No zoom or text selection yet.** Page renders at a fixed
  longest-edge of 1200 px. Rail mode, annotations, outline panel, and
  text search are all 0.4.0+ features.
- **WASM end-to-end not yet visually verified in this turn.** Build is
  clean, dev server returns HTTP 200 with the Avalonia splash, but the
  PDF-open → render round-trip in an actual browser hasn't been
  exercised in CI. Manual smoke recommended (see README "Build & run").

## 0.2.0

Wires `RailReader.Core.PdfPig` 0.6.0 (the pure-managed parser package
landed in RailReaderCore 0.6.0) alongside the existing `RailReader.Core`
dep. Both family packages now resolve and load in the WASM compile path.
No real PDF UI yet — that's v0.3.0 once a small embedded sample fixture
goes in. The shell now surfaces both packages' assembly versions in the
chrome.

### Changed

- `Directory.Packages.props` — bumped `RailReader.Core` 0.5.1 → 0.6.0,
  added `RailReader.Core.PdfPig` 0.6.0 (pinned exact).
- `RailReaderLite.csproj` — added `RailReader.Core.PdfPig` package
  reference.
- `MainViewModel` — new `PdfPigVersion` observable property; proves the
  pure-managed parser is consumable from net10.0-browser by reading the
  package's assembly version (linker-forced resolve).
- `MainView` — version footer now shows `Lite · Core · PdfPig`.
- Smoke tests — added a `PdfPig_text_service_constructs` assertion to
  prove the parser package is wired up.

## 0.1.0

Initial scaffold. Avalonia 12 Browser app boots in WebAssembly and consumes
the published `RailReader.Core` 0.5.1 NuGet package. No PDF functionality
yet — toolchain proof only.

### Added

- `src/RailReaderLite/` — shared Avalonia application (App, MainView,
  MainViewModel, ViewLocator).
- `src/RailReaderLite.Browser/` — WebAssembly entry point (Program.cs,
  wwwroot/index.html, main.js).
- `tests/RailReaderLite.Tests/` — xUnit project targeting net10.0.
- `Directory.Build.props` / `Directory.Packages.props` — central package
  management; single `VersionPrefix`.
- GitHub Actions `build.yml` — restore/build/test on push + PR.
- MIT `LICENSE`.
