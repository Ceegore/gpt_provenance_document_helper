using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class RequestStartRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_WithinLimits_AcquiresImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        using var limiter = new RequestStartRateLimiter(5, 5, () => now);

        using var handle1 = await limiter.AcquireAsync();
        using var handle2 = await limiter.AcquireAsync();

        Assert.NotNull(handle1);
        Assert.NotNull(handle2);
    }

    [Fact]
    public async Task AcquireAsync_ExceedingConcurrency_BlocksUntilRelease()
    {
        using var limiter = new RequestStartRateLimiter(10, 1);

        var handle1 = await limiter.AcquireAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await limiter.AcquireAsync(cts.Token);
        });

        handle1.Dispose();

        // After disposing handle1, acquiring succeeds
        using var handle2 = await limiter.AcquireAsync();
        Assert.NotNull(handle2);
    }

    [Fact]
    public void Properties_ExposeConstructorParameters()
    {
        using var limiter = new RequestStartRateLimiter(45, 8);
        Assert.Equal(45, limiter.StartsPerMinute);
        Assert.Equal(8, limiter.MaxConcurrency);
    }

    [Theory]
    [InlineData(0, 5, "startsPerMinute", "Starts per minute must be positive")]
    [InlineData(-1, 5, "startsPerMinute", "Starts per minute must be positive")]
    [InlineData(5, 0, "maxConcurrency", "Max concurrency must be positive")]
    [InlineData(5, -1, "maxConcurrency", "Max concurrency must be positive")]
    public void Constructor_InvalidParameters_ThrowsArgumentOutOfRangeException(int startsPerMinute, int maxConcurrency, string expectedParam, string expectedMsg)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new RequestStartRateLimiter(startsPerMinute, maxConcurrency));
        Assert.Equal(expectedParam, ex.ParamName);
        Assert.Contains(expectedMsg, ex.Message);
    }

    [Fact]
    public async Task Constructor_NullClock_UsesUtcNow()
    {
        using var limiter = new RequestStartRateLimiter(10, 5, null);
        using var handle = await limiter.AcquireAsync();
        Assert.NotNull(handle);
    }

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        var limiter = new RequestStartRateLimiter(10, 5);
        limiter.Dispose();
        limiter.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_RateLimitExceeded_WaitsForWindow()
    {
        var now = DateTimeOffset.UtcNow;
        using var limiter = new RequestStartRateLimiter(2, 5, () => now);

        using var h1 = await limiter.AcquireAsync();
        using var h2 = await limiter.AcquireAsync();

        // 3rd request exceeds 2 starts per minute, should block
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(cts.Token));

        // Advance time past 60s window
        now = now.AddSeconds(61);
        using var h3 = await limiter.AcquireAsync();
        Assert.NotNull(h3);
    }

    [Fact]
    public async Task AcquireAsync_RateLimitExceeded_ShortWaitDurationClampedTo10Ms()
    {
        var now = DateTimeOffset.UtcNow;
        var callCount = 0;
        using var limiter = new RequestStartRateLimiter(1, 5, () =>
        {
            callCount++;
            if (callCount == 1) return now; // First acquire
            if (callCount == 2) return now.AddSeconds(59).AddMilliseconds(995); // waitDuration = 5ms < 10ms -> clamped
            return now.AddSeconds(65); // Next iteration after delay
        });

        using var h1 = await limiter.AcquireAsync();
        using var h2 = await limiter.AcquireAsync();
        Assert.NotNull(h2);
    }

    [Fact]
    public async Task AcquireAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var limiter = new RequestStartRateLimiter(10, 5);
        limiter.Dispose();
        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => limiter.AcquireAsync());
        Assert.Equal(typeof(RequestStartRateLimiter).FullName, ex.ObjectName);
    }

    [Fact]
    public async Task Releaser_DisposeMultipleTimes_ReleasesOnlyOnce()
    {
        using var limiter = new RequestStartRateLimiter(10, 1);
        var handle = await limiter.AcquireAsync();
        handle.Dispose();
        handle.Dispose(); // safe, releases only once

        using var handle2 = await limiter.AcquireAsync();
        Assert.NotNull(handle2);
    }

    [Fact]
    public async Task AcquireAsync_CancellationDuringRateLimitWait_ReleasesConcurrencySlot()
    {
        var now = DateTimeOffset.UtcNow;
        using var limiter = new RequestStartRateLimiter(1, 1, () => now);

        var h1 = await limiter.AcquireAsync();
        h1.Dispose(); // Concurrency slot returned, but timestamp is still in window

        // Concurrency is free, but rate limit (1/min) is exhausted
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(cts.Token));

        // Advance time: verify concurrency slot was released and can be re-acquired
        now = now.AddSeconds(61);
        using var h2 = await limiter.AcquireAsync();
        Assert.NotNull(h2);
    }

    [Fact]
    public async Task AcquireAsync_PrunesMultipleExpiredTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        using var limiter = new RequestStartRateLimiter(2, 5, () => now);

        var h1 = await limiter.AcquireAsync();
        var h2 = await limiter.AcquireAsync();
        h1.Dispose();
        h2.Dispose();

        now = now.AddSeconds(61);
        using var h3 = await limiter.AcquireAsync();
        using var h4 = await limiter.AcquireAsync();
        Assert.NotNull(h3);
        Assert.NotNull(h4);
    }

    [Fact]
    public async Task AcquireAsync_Exact60SecondsBoundary_PrunesOldTimestampWithoutDelay()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        using var limiter = new RequestStartRateLimiter(1, 5, () => now);

        using var h1 = await limiter.AcquireAsync();

        // Exactly 60 seconds later: now.AddSeconds(-60) == original now
        now = now.AddSeconds(60);

        // Window boundary <= windowStart should prune the timestamp and acquire immediately
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var h2 = await limiter.AcquireAsync(cts.Token);
        Assert.NotNull(h2);
    }
}
