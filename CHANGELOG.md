# Changelog

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
