using System.Net;
using System.Net.Http.Headers;
using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void IsRetryableStatusCode_ClassifiesCorrectly(HttpStatusCode code, bool expectedRetryable)
    {
        var result = RetryPolicy.IsRetryableStatusCode(code);
        Assert.Equal(expectedRetryable, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidMaxAttempts_ThrowsArgumentOutOfRangeException(int maxAttempts)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(maxAttempts));
        Assert.Equal("maxAttempts", ex.ParamName);
        Assert.Contains("Max attempts must be at least 1", ex.Message);
    }

    [Fact]
    public void GetDelay_WithRetryAfterDelta_RespectsDelta()
    {
        var policy = new RetryPolicy(3);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(15));

        var delay = policy.GetDelay(1, response.Headers);

        Assert.Equal(TimeSpan.FromSeconds(15), delay);
    }

    [Fact]
    public void GetDelay_WithExcessiveRetryAfterDelta_ClampsToMaxRetryAfterDelay()
    {
        var policy = new RetryPolicy(3);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));

        var delay = policy.GetDelay(1, response.Headers);

        Assert.Equal(RetryPolicy.MaxRetryAfterDelay, delay);
    }

    [Fact]
    public void GetDelay_WithFutureRetryAfterDate_ClampsToMaxRetryAfterDelay()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new RetryPolicy(3, timeProvider: () => now);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddDays(5));

        var delay = policy.GetDelay(1, response.Headers);

        Assert.Equal(RetryPolicy.MaxRetryAfterDelay, delay);
    }

    [Fact]
    public void GetDelay_WithNearFutureRetryAfterDate_UsesDateDifference()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new RetryPolicy(3, timeProvider: () => now);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(12));

        var delay = policy.GetDelay(1, response.Headers);

        Assert.Equal(TimeSpan.FromSeconds(12), delay);
    }

    [Fact]
    public void GetDelay_WithoutHeaders_AppliesBackoff()
    {
        var random = new Random(42);
        var policy = new RetryPolicy(3, random);

        var delay1 = policy.GetDelay(1);
        var delay2 = policy.GetDelay(2);
        var delay3 = policy.GetDelay(3);

        Assert.True(delay1 >= TimeSpan.FromSeconds(2.0) && delay1 < TimeSpan.FromSeconds(3.0));
        Assert.True(delay2 >= TimeSpan.FromSeconds(5.0) && delay2 < TimeSpan.FromSeconds(7.0));
        Assert.True(delay3 >= TimeSpan.FromSeconds(10.0) && delay3 < TimeSpan.FromSeconds(13.0));
    }

    [Fact]
    public void IsRetryableException_TimeoutOrHttpRequest_ReturnsTrue()
    {
        Assert.True(RetryPolicy.IsRetryableException(new HttpRequestException("network down")));
        Assert.True(RetryPolicy.IsRetryableException(new TimeoutException("timed out")));
        Assert.True(RetryPolicy.IsRetryableException(new TaskCanceledException("timed out", new TimeoutException())));
    }

    [Fact]
    public void IsRetryableException_UserCancellation_ReturnsFalse()
    {
        Assert.False(RetryPolicy.IsRetryableException(new TaskCanceledException("user cancelled")));
        Assert.False(RetryPolicy.IsRetryableException(new OperationCanceledException("user cancelled")));
        Assert.False(RetryPolicy.IsRetryableException(new InvalidOperationException("bad state")));
    }

    [Fact]
    public void GetDelay_RetryAfterDelta_BoundaryConditions()
    {
        var policy = new RetryPolicy(3);

        // Zero delta -> falls back to exponential backoff
        using var respZero = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        respZero.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var zeroDelay = policy.GetDelay(1, respZero.Headers);
        Assert.True(zeroDelay >= TimeSpan.FromSeconds(2));

        // Exactly MaxRetryAfterDelay
        using var respMax = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        respMax.Headers.RetryAfter = new RetryConditionHeaderValue(RetryPolicy.MaxRetryAfterDelay);
        Assert.Equal(RetryPolicy.MaxRetryAfterDelay, policy.GetDelay(1, respMax.Headers));

        // Negative delta -> falls back to exponential backoff
        using var respNeg = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        respNeg.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(-5));
        var negDelay = policy.GetDelay(1, respNeg.Headers);
        Assert.True(negDelay > TimeSpan.Zero);
    }

    [Fact]
    public void GetDelay_RetryAfterDate_BoundaryConditions()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new RetryPolicy(3, timeProvider: () => now);

        // Past date -> falls back to exponential backoff
        using var respPast = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        respPast.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-5));
        var pastDelay = policy.GetDelay(1, respPast.Headers);
        Assert.True(pastDelay > TimeSpan.Zero);

        // Exactly now (delta 0) -> falls back to exponential backoff
        using var respNow = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        respNow.Headers.RetryAfter = new RetryConditionHeaderValue(now);
        var nowDelay = policy.GetDelay(1, respNow.Headers);
        Assert.True(nowDelay >= TimeSpan.FromSeconds(2));

        // Exactly MaxRetryAfterDelay in future
        using var respExactMax = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        respExactMax.Headers.RetryAfter = new RetryConditionHeaderValue(now + RetryPolicy.MaxRetryAfterDelay);
        Assert.Equal(RetryPolicy.MaxRetryAfterDelay, policy.GetDelay(1, respExactMax.Headers));
    }

    [Fact]
    public void GetDelay_WithCustomRandom_UsesCustomRandom()
    {
        var random = new Random(12345);
        var policy = new RetryPolicy(3, random);
        var delay = policy.GetDelay(1);

        var expectedJitter = 2.0 * 0.25 * new Random(12345).NextDouble();
        var expectedDelay = TimeSpan.FromSeconds(2.0 + expectedJitter);
        Assert.Equal(expectedDelay, delay);
    }
}
