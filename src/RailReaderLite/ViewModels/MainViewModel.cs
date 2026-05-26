using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core.PdfPig;
using RailReader.Core.Services;

namespace RailReaderLite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Resolving a constant from RailReader.Core forces the linker to actually
    // resolve that dep on the WASM compile path. Constructing a PdfPig service
    // does the same for the pure-managed parser package — proves both halves
    // of the family are consumable from net10.0-browser. Replace with real
    // wiring once the embedded-sample-PDF UI lands.
    [ObservableProperty]
    private string _coreVersion =
        typeof(LayoutConstants).Assembly.GetName().Version?.ToString() ?? "unknown";

    [ObservableProperty]
    private string _pdfPigVersion =
        typeof(PdfTextService).Assembly.GetName().Version?.ToString() ?? "unknown";

    [ObservableProperty]
    private string _liteVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
