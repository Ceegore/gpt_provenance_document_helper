namespace AssetProvenanceHelper.Core.Generation;

public sealed class RequestStartRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly int _startsPerMinute;
    private readonly Queue<DateTimeOffset> _startTimestamps = new();
    private readonly object _rateLock = new();
    private readonly Func<DateTimeOffset> _timeProvider;
    private bool _disposed;

    public RequestStartRateLimiter(
        int startsPerMinute,
        int maxConcurrency,
        Func<DateTimeOffset>? timeProvider = null)
    {
        if (startsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startsPerMinute), startsPerMinute, "Starts per minute must be positive.");
        }

        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency, "Max concurrency must be positive.");
        }

        _startsPerMinute = startsPerMinute;
        MaxConcurrency = maxConcurrency;
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public int MaxConcurrency { get; }
    public int StartsPerMinute => _startsPerMinute;

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Acquire concurrency slot
        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 2. Throttle start rate
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan waitDuration = TimeSpan.Zero;

                lock (_rateLock)
                {
                    var now = _timeProvider();
                    var windowStart = now.AddSeconds(-60);

                    while (_startTimestamps.Count > 0 && _startTimestamps.Peek() <= windowStart)
                    {
                        _startTimestamps.Dequeue();
                    }

                    if (_startTimestamps.Count < _startsPerMinute)
                    {
                        _startTimestamps.Enqueue(now);
                        return new Releaser(this);
                    }

                    var oldest = _startTimestamps.Peek();
                    waitDuration = (oldest.AddSeconds(60)) - now;
                    if (waitDuration < TimeSpan.FromMilliseconds(10))
                    {
                        waitDuration = TimeSpan.FromMilliseconds(10);
                    }
                }

                if (waitDuration > TimeSpan.Zero)
                {
                    await Task.Delay(waitDuration, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            ReleaseConcurrency();
            throw;
        }
    }

    private void ReleaseConcurrency()
    {
        if (!_disposed)
        {
            try
            {
                _concurrencySemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed during release
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _concurrencySemaphore.Dispose();
        }
    }

    private sealed class Releaser(RequestStartRateLimiter limiter) : IDisposable
    {
        private RequestStartRateLimiter? _limiter = limiter;

        public void Dispose()
        {
            Interlocked.Exchange(ref _limiter, null)?.ReleaseConcurrency();
        }
    }
}
