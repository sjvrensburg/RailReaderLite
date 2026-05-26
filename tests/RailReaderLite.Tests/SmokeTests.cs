using RailReader.Core.Services;
using RailReaderLite.ViewModels;
using Xunit;

namespace RailReaderLite.Tests;

public class SmokeTests
{
    [Fact]
    public void MainViewModel_constructs_in_empty_state()
    {
        // Proves the shared project + both family deps (Core,
        // Core.PdfPig, Renderer.PdfPigSkia) resolve. Browser project
        // itself can't be unit-tested here (net10.0-browser can't host
        // xUnit), so the smoke test exercises the shared half.
        var vm = new MainViewModel();
        Assert.False(vm.HasDocument);
        Assert.False(vm.CanPrev);
        Assert.False(vm.CanNext);
        Assert.Equal(0, vm.CurrentPage);
        Assert.Equal(0, vm.PageCount);
        Assert.Equal("—", vm.PageLabel);
        Assert.Null(vm.PageImage);
        Assert.False(vm.IsBusy);

        // Outline state in empty doc: no entries, panel hidden.
        Assert.Empty(vm.Outline);
        Assert.False(vm.HasOutline);
        Assert.False(vm.OutlineVisible);
    }

    [Fact]
    public void ToggleOutline_flips_visibility()
    {
        var vm = new MainViewModel();
        Assert.False(vm.OutlineVisible);
        vm.ToggleOutlineCommand.Execute(null);
        Assert.True(vm.OutlineVisible);
        vm.ToggleOutlineCommand.Execute(null);
        Assert.False(vm.OutlineVisible);
    }

    [Fact]
    public void Core_layout_constants_are_reachable()
    {
        // Sanity: a public symbol from RailReader.Core resolves at test-host
        // time. If this breaks, the Lite consumer is misconfigured.
        Assert.True(LayoutConstants.ConfidenceThreshold > 0);
    }

    [Fact]
    public void PdfPig_text_service_constructs()
    {
        // Sanity: the pure-managed parser package is consumable.
        var svc = new RailReader.Core.PdfPig.PdfTextService();
        Assert.NotNull(svc);
    }

    [Fact]
    public void PdfPigSkia_factory_constructs()
    {
        // Sanity: the rasterisation renderer package is consumable
        // and exposes the full IPdfServiceFactory surface.
        var factory = new RailReader.Renderer.PdfPigSkia.PdfPigSkiaPdfServiceFactory();
        Assert.NotNull(factory.CreatePdfTextService());
        Assert.NotNull(factory.CreatePdfLinkService());
    }
}
