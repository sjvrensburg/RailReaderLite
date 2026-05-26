# Changelog

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
