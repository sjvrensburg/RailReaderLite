# Changelog

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
