using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core.Services;

namespace RailReaderLite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Reading a constant from RailReader.Core forces the linker to actually
    // resolve the dep on the WASM compile path. Pure smoke-test for the
    // toolchain — replace with real wiring when Core.PdfPig lands.
    [ObservableProperty]
    private string _coreVersion =
        typeof(LayoutConstants).Assembly.GetName().Version?.ToString() ?? "unknown";

    [ObservableProperty]
    private string _liteVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
