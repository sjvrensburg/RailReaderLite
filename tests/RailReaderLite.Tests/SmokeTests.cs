using RailReader.Core.Services;
using RailReaderLite.ViewModels;
using Xunit;

namespace RailReaderLite.Tests;

public class SmokeTests
{
    [Fact]
    public void MainViewModel_constructs()
    {
        // Proves the shared project + its RailReader.Core dep resolve. The
        // Browser project itself can't be unit-tested here (net10.0-browser
        // can't host xUnit), so the smoke test exercises the shared half.
        var vm = new MainViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.CoreVersion));
        Assert.False(string.IsNullOrWhiteSpace(vm.LiteVersion));
    }

    [Fact]
    public void Core_layout_constants_are_reachable()
    {
        // Sanity: a public symbol from RailReader.Core resolves at test-host
        // time. If this breaks, the Lite consumer is misconfigured.
        Assert.True(LayoutConstants.ConfidenceThreshold > 0);
    }
}
