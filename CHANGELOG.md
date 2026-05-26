# Changelog

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
