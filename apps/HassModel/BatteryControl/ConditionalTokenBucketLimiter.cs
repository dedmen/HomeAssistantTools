using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;


namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    public class ConditionalTokenBucketLimiter : RateLimiter
    {
        private readonly int _maxTokens;
        private readonly int _tokensPerReplenishment;
        private readonly TimeSpan _replenishmentPeriod;
        private readonly Timer _replenishmentTimer;
        private readonly object _lock = new();

        private int _currentTokens;
        private int _activeRequestsCount;
        private long _failedRequestsCount; // Tracked for statistics
        private bool _isDisposed;

        // Waiting async requests that want to take the next token
        private readonly Queue<WaitingRequest> _waitingRequests = new();

        private class WaitingRequest
        {
            public int PermitCount { get; }
            public TaskCompletionSource<RateLimitLease> Tcs { get; }
            public CancellationTokenRegistration CancellationRegistration { get; set; }

            public WaitingRequest(int permitCount)
            {
                PermitCount = permitCount;
                // RunContinuationsAsynchronously verhindert Deadlocks bei synchronen Fortsetzungen im Lock
                Tcs = new TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }


        public override TimeSpan? IdleDuration => null;

        public ConditionalTokenBucketLimiter(int maxTokens, int tokensPerReplenishment, TimeSpan replenishmentPeriod)
        {
            _maxTokens = maxTokens;
            _currentTokens = maxTokens;
            _tokensPerReplenishment = tokensPerReplenishment;
            _replenishmentPeriod = replenishmentPeriod;

            _replenishmentTimer = new Timer(ReplenishTokens, null, replenishmentPeriod, replenishmentPeriod);
        }

        public override RateLimiterStatistics? GetStatistics()
        {
            lock (_lock)
            {
                return new RateLimiterStatistics
                {
                    CurrentAvailablePermits = _currentTokens,
                    CurrentQueuedCount = 0, // 0 since this basic version doesn't queue
                    TotalFailedLeases = Interlocked.Read(ref _failedRequestsCount),
                    TotalSuccessfulLeases = 0 // Optional metric, often left default depending on dotnet version requirements
                };
            }
        }

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            lock (_lock)
            {
                // Async waiters get priority, if anyone is waiting, they get fulfilled first
                if (_waitingRequests.Count == 0 && _currentTokens >= permitCount)
                {
                    _currentTokens -= permitCount;
                    Interlocked.Increment(ref _activeRequestsCount);
                    return new CustomLease(true, this);
                }

                // Track failed attempts for statistics
                Interlocked.Increment(ref _failedRequestsCount);
                return new CustomLease(false, this);
            }
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_isDisposed)
                {
                    return ValueTask.FromResult<RateLimitLease>(new CustomLease(false, this));
                }

                // Drop all other waiters, we (the latest request) will replace them
                DropAllAsyncRequests();

                // Immediate success if no-one else is waiting, and we have tokens
                if (_waitingRequests.Count == 0 && _currentTokens >= permitCount)
                {
                    _currentTokens -= permitCount;
                    Interlocked.Increment(ref _activeRequestsCount);
                    return ValueTask.FromResult<RateLimitLease>(new CustomLease(true, this));
                }

                // Not enough tokens, put us into the waiting queue, we will then win the next token that becomes available
                var waitingRequest = new WaitingRequest(permitCount);

                if (cancellationToken.CanBeCanceled)
                {
                    waitingRequest.CancellationRegistration = cancellationToken.Register(state =>
                    {
                        var req = (WaitingRequest)state!;
                        lock (_lock)
                        {
                            if (req.Tcs.TrySetCanceled(cancellationToken))
                            {
                                // We process that inside ReplenishTokens, by dropping the request
                            }
                        }
                    }, waitingRequest);
                }

                _waitingRequests.Enqueue(waitingRequest);
                return new ValueTask<RateLimitLease>(waitingRequest.Tcs.Task);
            }
        }

        public void DropAllAsyncRequests()
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                if (Volatile.Read(ref _activeRequestsCount) > 0)
                {
                    return;
                }

                // Process waiting async acquire's while we have enough tokens
                while (_waitingRequests.Count > 0)
                {
                    var nextRequest = _waitingRequests.Peek();

                    // If the task is cancelled, just skip it
                    if (nextRequest.Tcs.Task.IsCanceled)
                    {
                        _waitingRequests.Dequeue();
                        nextRequest.CancellationRegistration.Dispose();
                        continue;
                    }

                    // "succeed" them with a failed lease
                    {
                        _waitingRequests.Dequeue();
                        nextRequest.CancellationRegistration.Dispose();
                        Interlocked.Increment(ref _failedRequestsCount);
                        nextRequest.Tcs.TrySetResult(new CustomLease(false, this));
                    }
                }

            }
        }

        private void ReplenishTokens(object? state)
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                if (Volatile.Read(ref _activeRequestsCount) > 0)
                {
                    return;
                }

                _currentTokens = Math.Min(_maxTokens, _currentTokens + _tokensPerReplenishment);

                // Process waiting async acquire's while we have enough tokens
                while (_waitingRequests.Count > 0)
                {
                    var nextRequest = _waitingRequests.Peek();

                    // If the task is cancelled, just skip it
                    if (nextRequest.Tcs.Task.IsCanceled)
                    {
                        _waitingRequests.Dequeue();
                        nextRequest.CancellationRegistration.Dispose();
                        continue;
                    }

                    // If we have enough tokens, approve the acquire
                    if (_currentTokens >= nextRequest.PermitCount)
                    {
                        _waitingRequests.Dequeue();
                        nextRequest.CancellationRegistration.Dispose();

                        _currentTokens -= nextRequest.PermitCount;
                        Interlocked.Increment(ref _activeRequestsCount);

                        nextRequest.Tcs.TrySetResult(new CustomLease(true, this));
                    }
                    else
                    {
                        break;
                    }
                }

            }
        }

        private void ReleaseRequest()
        {
            Interlocked.Decrement(ref _activeRequestsCount);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _replenishmentTimer.Dispose();
                }
                _isDisposed = true;
            }
            base.Dispose(disposing);
        }

        private class CustomLease : RateLimitLease
        {
            private readonly ConditionalTokenBucketLimiter _limiter;
            private readonly bool _isAcquired;

            public override bool IsAcquired => _isAcquired;
            public override System.Collections.Generic.IEnumerable<string> MetadataNames => Array.Empty<string>();

            public CustomLease(bool isAcquired, ConditionalTokenBucketLimiter limiter)
            {
                _isAcquired = isAcquired;
                _limiter = limiter;
            }

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                metadata = null;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (_isAcquired && disposing)
                {
                    _limiter.ReleaseRequest();
                }
                base.Dispose(disposing);
            }
        }
    }

}
