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
    public void Search_is_disabled_until_a_document_is_open_and_query_is_set()
    {
        // 0.5.0 added search + selection. SearchCommand is gated on
        // both HasDocument and a non-empty query. With no document and
        // no query at construction time it must not be executable.
        var vm = new MainViewModel();
        Assert.False(vm.SearchCommand.CanExecute(null));
        vm.SearchQuery = "hello"; // still no document
        Assert.False(vm.SearchCommand.CanExecute(null));

        Assert.Equal(-1, vm.CurrentMatchIndex);
        Assert.Equal(0, vm.MatchCount);
        Assert.False(vm.NextMatchCommand.CanExecute(null));
        Assert.False(vm.PrevMatchCommand.CanExecute(null));
    }

    [Fact]
    public void Core_layout_constants_are_reachable()
    {
        // Sanity: a public symbol from RailReader.Core resolves at test-host
        // time. If this breaks, the Lite consumer is misconfigured.
        Assert.True(LayoutConstants.ConfidenceThreshold > 0);
    }

    // v0.7.0 dropped both RailReader.Core.PdfPig and
    // RailReader.Renderer.PdfPigSkia — Lite's PDF backend is now PDF.js
    // (browser-only, exercised via JSInterop), so package-consumability
    // smoke tests for those don't apply any more. XYCutPlusPlusResolver
    // and LayoutBlock from RailReader.Core are still in use; the
    // Core_layout_constants_are_reachable test above already covers
    // that surface.

    [Fact]
    public void Zoom_defaults_to_1_and_commands_gate_on_document_state()
    {
        // 0.5.3+: manual zoom shape. At construction the page is in fit
        // mode (Zoom == 1.0 → Stretch.Uniform/DownOnly); zoom commands
        // are inert until a document is loaded.
        var vm = new MainViewModel();
        Assert.Equal(1.0, vm.Zoom);
        Assert.Equal("100%", vm.ZoomPercent);
        Assert.Equal(Avalonia.Media.Stretch.Uniform, vm.ImageStretch);
        Assert.Equal(Avalonia.Media.StretchDirection.DownOnly, vm.ImageStretchDirection);

        Assert.False(vm.ZoomInCommand.CanExecute(null));
        Assert.False(vm.ZoomOutCommand.CanExecute(null));
        Assert.False(vm.ZoomResetCommand.CanExecute(null));
    }

    [Fact]
    public void RailMode_is_off_in_empty_state_and_when_zoom_is_below_threshold()
    {
        // 0.6.0+: rail mode is gated on (HasDocument && Zoom > 1.4 &&
        // analysed page has blocks). Empty VM never satisfies the
        // analysis half — IsRailMode must be false and the rail-nav
        // commands must be disabled.
        var vm = new MainViewModel();
        Assert.False(vm.IsRailMode);
        Assert.False(vm.RailNextLineCommand.CanExecute(null));
        Assert.False(vm.RailPrevLineCommand.CanExecute(null));
        Assert.Equal(-1, vm.CurrentBlockIndex);
        Assert.Equal(-1, vm.CurrentLineIndex);
        Assert.Null(vm.ActiveBlockBoundsPagePoints);
        Assert.Null(vm.ActiveLineBoundsPagePoints);
        Assert.Equal("", vm.RailStatus);

        // Cranking the zoom past the threshold without a document
        // doesn't engage rail mode.
        vm.Zoom = 2.0;
        Assert.False(vm.IsRailMode);
        Assert.False(vm.RailNextLineCommand.CanExecute(null));
    }

    [Fact]
    public void Zoom_clamps_to_range_and_flips_stretch_above_1()
    {
        // Direct mutation of Zoom: out-of-range values clamp to
        // [MinZoom=1.0, MaxZoom=3.0] inside OnZoomChanged. Above 1.0
        // the View should switch to Stretch.None so the now-larger
        // bitmap displays at natural pixel size and the ScrollViewer
        // scrolls.
        var vm = new MainViewModel();

        vm.Zoom = 1.5;
        Assert.Equal(1.5, vm.Zoom);
        Assert.Equal("150%", vm.ZoomPercent);
        Assert.Equal(Avalonia.Media.Stretch.None, vm.ImageStretch);

        vm.Zoom = 10.0;
        Assert.Equal(4.0, vm.Zoom);  // clamped to MaxZoom

        vm.Zoom = 0.1;
        Assert.Equal(1.0, vm.Zoom);  // clamped to MinZoom
        Assert.Equal(Avalonia.Media.Stretch.Uniform, vm.ImageStretch);
    }
}
