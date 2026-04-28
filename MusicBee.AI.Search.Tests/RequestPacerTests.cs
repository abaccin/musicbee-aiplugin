using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using MusicBee.AI.Search.AI;

namespace MusicBee.AI.Search.Tests;

public class RequestPacerTests
{
    [Fact]
    public void AdaptiveRateLimiter_HalvesOnRateLimited_DownToFloor()
    {
        var lim = new AdaptiveRateLimiter(initialRpm: 60, ceiling: 60, floor: 4);
        lim.OnRateLimited(null);
        lim.CurrentRpm.Should().Be(30);
        for (int i = 0; i < 10; i++) lim.OnRateLimited(null);
        lim.CurrentRpm.Should().Be(4);
    }

    [Fact]
    public async Task AdaptiveRateLimiter_HonoursRetryAfter()
    {
        var lim = new AdaptiveRateLimiter(initialRpm: 6000, ceiling: 6000, floor: 100);
        var ra = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(150));
        lim.OnRateLimited(ra);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lim.WaitForSlotAsync(CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(120);
    }
}
