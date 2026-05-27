using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using RailReaderLite;
using RailReaderLite.Browser.Interop;
using RailReaderLite.Services;

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        // Publish the PDF.js runtime singleton before the App boots so
        // MainViewModel sees a non-null PdfJsRuntimeRegistry.Current.
        PdfJsRuntimeRegistry.Current = new PdfJsRuntime();
        return BuildAvaloniaApp()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}