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
}
