using System.Net;
using System.Net.Http.Headers;

namespace AssetProvenanceHelper.Core.Generation;

public sealed class RetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromSeconds(60);

    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout, // 408
        HttpStatusCode.TooManyRequests, // 429
        HttpStatusCode.InternalServerError, // 500
        HttpStatusCode.BadGateway, // 502
        HttpStatusCode.ServiceUnavailable, // 503
        HttpStatusCode.GatewayTimeout // 504
    ];

    private readonly Random? _random;
    private readonly Func<DateTimeOffset> _timeProvider;

    public RetryPolicy(
        int maxAttempts = DefaultMaxAttempts,
        Random? random = null,
        Func<DateTimeOffset>? timeProvider = null)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Max attempts must be at least 1.");
        }

        MaxAttempts = maxAttempts;
        _random = random;
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public int MaxAttempts { get; }

    public static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return RetryableStatusCodes.Contains(statusCode);
    }

    public static bool IsRetryableException(Exception exception)
    {
        if (exception is TaskCanceledException tce)
        {
            return tce.InnerException is TimeoutException;
        }

        return exception is HttpRequestException or TimeoutException;
    }

    public TimeSpan GetDelay(int attempt, HttpResponseHeaders? responseHeaders = null)
    {
        if (responseHeaders?.RetryAfter != null)
        {
            if (responseHeaders.RetryAfter.Delta.HasValue && responseHeaders.RetryAfter.Delta.Value > TimeSpan.Zero)
            {
                var delta = responseHeaders.RetryAfter.Delta.Value;
                return delta > MaxRetryAfterDelay ? MaxRetryAfterDelay : delta;
            }

            if (responseHeaders.RetryAfter.Date.HasValue)
            {
                var delay = responseHeaders.RetryAfter.Date.Value - _timeProvider();
                if (delay > TimeSpan.Zero)
                {
                    return delay > MaxRetryAfterDelay ? MaxRetryAfterDelay : delay;
                }
            }
        }

        // Base delays: attempt 1 -> 2s, attempt 2 -> 5s, attempt 3+ -> 10s
        var baseSeconds = attempt switch
        {
            1 => 2.0,
            2 => 5.0,
            _ => 10.0
        };

        // Add 0%..25% jitter
        var nextDouble = _random?.NextDouble() ?? Random.Shared.NextDouble();
        var jitter = baseSeconds * 0.25 * nextDouble;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }
}
