# RailReaderLite

Web (Avalonia.Browser / WebAssembly) front-end for the
[RailReaderCore](https://github.com/sjvrensburg/RailReaderCore) library set.
Same business logic as the desktop
[railreader2](https://github.com/sjvrensburg/railreader2), targeting the
browser instead of native.

**Status:** v0.1.0 — toolchain proof only. The Avalonia.Browser shell builds
and runs, and consumes the published `RailReader.Core` NuGet package. PDF
rendering and rail-mode features land in 0.2.0+, gated on the
`RailReader.Core.PdfPig` sibling package (RailReaderCore
[issue #11](https://github.com/sjvrensburg/RailReaderCore/issues/11)).

## Project layout

```
src/
├── RailReaderLite/          ← shared Avalonia application (UI + view models)
└── RailReaderLite.Browser/  ← WebAssembly entry point (Program.cs, wwwroot)
tests/
└── RailReaderLite.Tests/    ← xUnit smoke tests (net10.0, not browser)
```

## Build & run

```bash
# Restore + build
dotnet build RailReaderLite.slnx -c Release

# Run the browser app locally (serves the WASM bundle on http://localhost:5xxx)
dotnet run --project src/RailReaderLite.Browser -c Release

# Tests
dotnet test tests/RailReaderLite.Tests -c Release
```

Prerequisites: .NET 10 SDK. The first browser run downloads the WASM workload
artifacts automatically.

## Consuming an unreleased RailReaderCore build

For development against a local RailReaderCore branch, follow the workflow
documented in RailReaderCore: pack locally, then point this repo at the
local feed via `NuGet.config`.

```bash
# 1. Pack RailReaderCore locally
cd ~/RailReaderCore
dotnet pack RailReaderCore.slnx -c Release -o /tmp/railreadercore-dist

# 2. Uncomment the local-railreadercore line in this repo's NuGet.config
# 3. Bump the RailReader.Core version in Directory.Packages.props to match
# 4. Restore again
cd ~/RailReaderLite
dotnet restore RailReaderLite.slnx
```

Don't commit the `NuGet.config` uncomment — the file is checked in only as a
documented template.

## Versioning

`Directory.Build.props` holds the single `VersionPrefix`. RailReaderLite is a
hosted app, not a NuGet package — no tag-triggered publishing pipeline.
Tagged releases optionally deploy the WASM bundle to GitHub Pages.

## License

MIT. See [`LICENSE`](LICENSE).

The companion proprietary mobile app (when it ships) lives in a separate
private repository and consumes the same MIT-licensed RailReaderCore
packages.
