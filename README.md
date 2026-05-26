# RailReaderLite

Web (Avalonia.Browser / WebAssembly) front-end for the
[RailReaderCore](https://github.com/sjvrensburg/RailReaderCore) library set.
Same business logic as the desktop
[railreader2](https://github.com/sjvrensburg/railreader2), targeting the
browser instead of native.

**Status:** v0.3.1 — first end-to-end working prototype. Open a PDF, render
pages, navigate. Built on `RailReader.Core.PdfPig` (parsing) and
`RailReader.Renderer.PdfPigSkia` (rasterisation), both pure-managed; no
PDFium, no ONNX. Zoom, outline panel, text selection, rail mode are
0.4.0+ features.

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

### Prerequisites

- .NET 10 SDK.
- The `wasm-tools` workload (Emscripten 3.1.56 toolchain) — needed to
  statically link `libSkiaSharp.a` + `libHarfBuzzSharp.a` into
  `dotnet.native.wasm`. Without it the bundle throws
  `System.DllNotFoundException: libSkiaSharp` at first frame:

  ```bash
  dotnet workload install wasm-tools
  ```

**Ubuntu sharp edge.** Ubuntu's apt-installed `dotnet-sdk-10` package
mishandles `dotnet workload install` — it reports success but the
manifest never persists. If you're on Ubuntu, install dotnet user-level
instead:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet

# Then put ~/.dotnet ahead of /usr/bin/dotnet on PATH (.bashrc):
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

dotnet workload install wasm-tools   # now lands in ~/.dotnet/
```

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
