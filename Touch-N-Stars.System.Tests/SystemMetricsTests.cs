using TouchNStars.Server;
using TouchNStars.Server.Services;
using Xunit;

public class SystemMetricsTests
{
    [Fact]
    public void SwapUsesActiveCountersAndConvertsKiBToBytes()
    {
        var result = SystemMetricsService.ParseLinuxMemory(new[] {
            "MemTotal: 8192 kB", "MemAvailable: 4096 kB",
            "SwapTotal: 2048 kB", "SwapFree: 512 kB", "SwapCached: 256 kB"
        });
        Assert.Equal(8192 * 1024, result.Memory.TotalBytes);
        Assert.Equal(2048 * 1024, result.Swap.TotalBytes);
        Assert.Equal(1536 * 1024, result.Swap.UsedBytes);
        Assert.Equal(512 * 1024, result.Swap.AvailableBytes);
        Assert.Equal(75, result.Swap.UsedPercent);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 150, 0, 0)]
    [InlineData(100, 0, 102400, 100)]
    public void DisabledSwapAndFreeCounterBounds(long total, long free, long used, double percent)
    {
        var swap = SystemMetricsService.ParseLinuxMemory(new[] {
            $"SwapTotal: {total} kB", $"SwapFree: {free} kB"
        }).Swap;
        Assert.Equal(used, swap.UsedBytes);
        Assert.Equal(percent, swap.UsedPercent);
    }

    [Theory]
    [InlineData("SwapFree: malformed kB")]
    [InlineData("SwapFree: -1 kB")]
    [InlineData("SwapFree: 9223372036854775807 kB")]
    [InlineData("unrelated counter")]
    public void UnavailableSwapIsNotReportedAsZeroUsage(string line)
    {
        Assert.Null(SystemMetricsService.ParseLinuxMemory(new[] { "SwapTotal: 2048 kB", line }).Swap);
    }

    [Fact]
    public void AvahiArgumentsPreserveNamesAndTxtWithoutShellOrHostPublication()
    {
        var info = AvahiServicePublisher.BuildStartInfo("--rig's name $(false)", "_touchnstars._tcp.", 5050,
            new Dictionary<string, string> { ["instanceName"] = "My rig", ["port"] = "5050" });
        Assert.False(info.UseShellExecute);
        // started through a shell that closes NINA's inherited descriptors first; the arguments reach the publisher as "$@"
        Assert.Equal("/bin/bash", info.FileName);
        Assert.EndsWith("exec /usr/bin/avahi-publish-service \"$@\"", AvahiServicePublisher.CloseDescriptorsAndRunPublisher);
        Assert.Equal(new[] { "-c", AvahiServicePublisher.CloseDescriptorsAndRunPublisher, "avahi-publish-service",
            "--no-fail", "--", "--rig's name $(false)", "_touchnstars._tcp", "5050",
            "txtvers=1", "instanceName=My rig", "port=5050" }, info.ArgumentList);
    }
}
