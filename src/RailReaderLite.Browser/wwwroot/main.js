import { dotnet } from './_framework/dotnet.js'

// Side-effect import: pdfjs-shim.mjs registers globalThis.RailReaderPdfJs
// before .NET runs, so the C# JSInterop layer can bind to it during
// runtime startup.
import './pdfjs-shim.mjs';

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();

await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
