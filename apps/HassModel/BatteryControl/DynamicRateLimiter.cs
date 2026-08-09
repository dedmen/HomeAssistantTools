using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    using System;
    using System.Threading;
    using System.Threading.RateLimiting;
    using System.Threading.Tasks;

    public class DynamicMagnitudeRateLimiter : RateLimiter
    {
        private readonly int _largeChangeThreshold;
        private readonly TimeSpan _maxDelay;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // Tracks the exact timestamp when the last change was officially applied
        private DateTimeOffset _lastExecutionTime = DateTimeOffset.UtcNow;

        private long _successfulLeasesCount;
        private long _failedLeasesCount;

        public override TimeSpan? IdleDuration => DateTimeOffset.UtcNow - _lastExecutionTime;

        public DynamicMagnitudeRateLimiter(int largeChangeThreshold, TimeSpan maxDelay)
        {
            _largeChangeThreshold = Math.Max(1, largeChangeThreshold);
            _maxDelay = maxDelay;
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            // Calculate how much delay this specific permit count requires
            TimeSpan requiredDelay = CalculateDelay(permitCount);

            // If it's a large change, it requires 0 delay and always passes immediately
            if (requiredDelay == TimeSpan.Zero)
            {
                _lastExecutionTime = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref _successfulLeasesCount);
                return TypeLease.SuccessfulLease;
            }

            // For small changes, check if enough time has passed since the last execution
            TimeSpan timeElapsedSinceLastUpdate = DateTimeOffset.UtcNow - _lastExecutionTime;

            if (timeElapsedSinceLastUpdate >= requiredDelay)
            {
                // Enough idle time has passed! We can apply this small change instantly.
                _lastExecutionTime = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref _successfulLeasesCount);
                return TypeLease.SuccessfulLease;
            }

            // Not enough time has passed yet; non-blocking attempt fails.
            Interlocked.Increment(ref _failedLeasesCount);
            return TypeLease.FailedLease;
        }

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                TimeSpan requiredDelay = CalculateDelay(permitCount);
                DateTimeOffset now = DateTimeOffset.UtcNow;

                // Determine when this update is allowed to execute
                DateTimeOffset allowedExecutionTime = _lastExecutionTime.Add(requiredDelay);
                TimeSpan actualWaitTime = allowedExecutionTime - now;

                // If we haven't waited long enough yet, asynchronously pause
                if (actualWaitTime > TimeSpan.Zero)
                {
                    await Task.Delay(actualWaitTime, cancellationToken);
                }

                _lastExecutionTime = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref _successfulLeasesCount);
                return TypeLease.SuccessfulLease;
            }
            catch
            {
                Interlocked.Increment(ref _failedLeasesCount);
                throw;
            }
            finally
            {
                _lock.Release();
            }
        }

        public override RateLimiterStatistics? GetStatistics()
        {
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = (DateTimeOffset.UtcNow - _lastExecutionTime) >= _maxDelay ? _largeChangeThreshold : 0,
                CurrentQueuedCount = _lock.CurrentCount == 0 ? 1 : 0,
                TotalSuccessfulLeases = Interlocked.Read(ref _successfulLeasesCount),
                TotalFailedLeases = Interlocked.Read(ref _failedLeasesCount)
            };
        }

        private TimeSpan CalculateDelay(int changeMagnitude)
        {
            if (changeMagnitude >= _largeChangeThreshold)
            {
                return TimeSpan.Zero;
            }

            double ratio = (double)changeMagnitude / _largeChangeThreshold;
            double delayFactor = 1.0 - ratio;

            return TimeSpan.FromMilliseconds(_maxDelay.TotalMilliseconds * delayFactor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lock.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class TypeLease : RateLimitLease
        {
            public static readonly TypeLease SuccessfulLease = new(true);
            public static readonly TypeLease FailedLease = new(false);

            private TypeLease(bool isAcquired) => IsAcquired = isAcquired;

            public override bool IsAcquired { get; }
            public override System.Collections.Generic.IEnumerable<string> MetadataNames => Array.Empty<string>();
            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                metadata = null;
                return false;
            }
        }
    }


}


//#TODO 

/*
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.RateLimiting;

public class Program
{
    public static async Task Main()
    {
        // 1. Configure the rate limiter
        var partitionLimiter = PartitionedRateLimiter.Create<HttpRequestMessage, string>(context =>
        {
            // We use a Fixed Window: 1 permit allowed every 5 seconds
            return RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = TimeSpan.FromSeconds(5),
                // QueueLimit = 0 means instantly reject/fail if the 5s window is busy
                QueueLimit = 0
            });
        });

        // 2. Create the handler and inject it into HttpClient
        var handler = new ClientRateLimitingHttpMessageHandler(partitionLimiter) // dotnet add package Microsoft.Extensions.Http.Resilience
        {
            InnerHandler = new HttpClientHandler()
        };

        using var httpClient = new HttpClient(handler);

        // 3. Test your requests
        try
        {
            Console.WriteLine("Sending Request 1...");
            var res1 = await httpClient.GetStringAsync("https://example.com");

            Console.WriteLine("Sending Request 2 immediately (Should fail)...");
            var res2 = await httpClient.GetStringAsync("https://example.com");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // The standard handler automatically throws an HTTP 429 when rate-limited locally
            Console.WriteLine("Blocked locally: Too Many Requests (429).");
        }
    }
}




using System;
using System.Net.Http;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

public class StandardRateLimitedClient : IDisposable
{
    private readonly HttpClient _httpClient = new HttpClient();

    // Allows exactly 1 request per 5-second window
    private readonly FixedWindowRateLimiter _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 1,
        Window = TimeSpan.FromSeconds(5),
        QueueLimit = 0, // 0 = Do not queue; fail instantly if a request is active or too recent
        AutoReplenish = true
    });

    public async Task<string> SendRequestAsync(string url)
    {
        // Attempt to acquire 1 permit instantly
        using RateLimitLease lease = await _limiter.AcquireAsync(permitCount: 1);

        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("Request blocked: Concurrency limit or 5-second window breached.");
        }

        return await _httpClient.GetStringAsync(url);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _limiter.Dispose();
    }
}
*/