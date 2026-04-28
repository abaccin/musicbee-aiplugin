using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search.AI
{
    /// <summary>
    /// Token-bucket-style rate limiter that adapts to 429 feedback.
    /// Starts at <see cref="Ceiling"/> RPM, halves on a 429 (down to
    /// <see cref="Floor"/>), and recovers gradually after sustained success.
    /// Honours <c>Retry-After</c> headers by enforcing a wall-clock pause
    /// before the next slot is granted.
    /// </summary>
    internal sealed class AdaptiveRateLimiter
    {
        private readonly object _gate = new object();
        private readonly int _floor;
        private int _currentRpm;
        private int _ceiling;
        private DateTime _nextSlotUtc = DateTime.MinValue;
        private DateTime _retryAfterUtc = DateTime.MinValue;
        private int _consecutiveSuccesses;

        public AdaptiveRateLimiter(int initialRpm, int ceiling, int floor)
        {
            _ceiling = Math.Max(1, ceiling);
            _floor = Math.Max(1, Math.Min(floor, _ceiling));
            _currentRpm = Math.Max(_floor, Math.Min(initialRpm, _ceiling));
        }

        public int Ceiling => _ceiling;
        public int Floor => _floor;
        public int CurrentRpm { get { lock (_gate) return _currentRpm; } }

        public void UpdateCeiling(int newCeiling)
        {
            lock (_gate)
            {
                _ceiling = Math.Max(1, newCeiling);
                if (_currentRpm > _ceiling) _currentRpm = _ceiling;
            }
        }

        /// <summary>
        /// Blocks until the next request slot is available. Honours
        /// <see cref="OnRateLimited"/>-supplied Retry-After delays.
        /// </summary>
        public async Task WaitForSlotAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan wait;
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    var target = _retryAfterUtc > _nextSlotUtc ? _retryAfterUtc : _nextSlotUtc;
                    if (target <= now)
                    {
                        var intervalMs = 60_000.0 / _currentRpm;
                        _nextSlotUtc = now.AddMilliseconds(intervalMs);
                        return;
                    }
                    wait = target - now;
                }
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }

        public void OnSuccess()
        {
            lock (_gate)
            {
                _consecutiveSuccesses++;
                if (_consecutiveSuccesses >= 20 && _currentRpm < _ceiling)
                {
                    _currentRpm = Math.Min(_ceiling, Math.Max(_currentRpm + 1, (int)(_currentRpm * 1.25)));
                    _consecutiveSuccesses = 0;
                }
            }
        }

        public void OnRateLimited(RetryConditionHeaderValue retryAfter)
        {
            lock (_gate)
            {
                _consecutiveSuccesses = 0;
                _currentRpm = Math.Max(_floor, _currentRpm / 2);
                var wait = ParseRetryAfter(retryAfter);
                if (wait > TimeSpan.Zero)
                {
                    var until = DateTime.UtcNow.Add(wait);
                    if (until > _retryAfterUtc) _retryAfterUtc = until;
                }
            }
        }

        public void OnTransientError() { lock (_gate) _consecutiveSuccesses = 0; }

        private static TimeSpan ParseRetryAfter(RetryConditionHeaderValue retryAfter)
        {
            if (retryAfter == null) return TimeSpan.Zero;
            if (retryAfter.Delta is TimeSpan d && d > TimeSpan.Zero) return d;
            if (retryAfter.Date is DateTimeOffset when_)
            {
                var diff = when_ - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero) return diff;
            }
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Single-sender request queue that paces outbound HTTP calls to a
    /// rate-limited backend. Subclasses implement <see cref="DispatchAsync"/>
    /// to define how queued items become HTTP requests; the base class
    /// handles slot acquisition, retry on 429/5xx with exponential backoff,
    /// and adaptive rate adjustment.
    /// </summary>
    /// <typeparam name="T">Per-call work item (usually wraps a TaskCompletionSource).</typeparam>
    internal abstract class RequestPacer<T> : IDisposable
    {
        private readonly BlockingCollection<T> _queue = new BlockingCollection<T>();
        private readonly Thread _worker;
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private readonly AdaptiveRateLimiter _limiter;
        private readonly string _laneName;
        private bool _disposed;

        protected HttpClient Http { get; }
        protected AdaptiveRateLimiter Limiter => _limiter;

        protected RequestPacer(string laneName, HttpClient http, int initialRpm, int ceilingRpm, int floorRpm)
        {
            _laneName = laneName;
            Http = http ?? throw new ArgumentNullException(nameof(http));
            _limiter = new AdaptiveRateLimiter(initialRpm, ceilingRpm, floorRpm);
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"RequestPacer:{laneName}"
            };
            _worker.Start();
        }

        public void UpdateCeiling(int newCeiling) => _limiter.UpdateCeiling(newCeiling);

        /// <summary>
        /// Enqueues a work item for sequential dispatch. Throws
        /// <see cref="ObjectDisposedException"/> if the pacer has been disposed.
        /// </summary>
        protected void Enqueue(T item)
        {
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
            _queue.Add(item);
        }

        /// <summary>
        /// Drains and dispatches a batch of items. Implementers should call
        /// <see cref="SendWithRetryAsync"/> to issue the HTTP request(s) and
        /// then complete each item's TCS. Exceptions from this method fault
        /// only the offered batch; the worker loop continues running.
        /// </summary>
        protected abstract Task DispatchAsync(IReadOnlyList<T> batch, CancellationToken ct);

        /// <summary>
        /// Number of items the worker may pull off the queue and pass into
        /// a single <see cref="DispatchAsync"/> call. Defaults to 1 (no
        /// batching). Override on lanes that can coalesce work.
        /// </summary>
        protected virtual int MaxBatchSize => 1;

        /// <summary>
        /// Called when the worker is faulting an item because of disposal or
        /// an unhandled dispatch error. Subclasses should set the underlying
        /// TaskCompletionSource to a faulted/cancelled state.
        /// </summary>
        protected abstract void FaultItem(T item, Exception ex);

        private void WorkerLoop()
        {
            try
            {
                foreach (var first in _queue.GetConsumingEnumerable(_shutdownCts.Token))
                {
                    var batch = new List<T>(MaxBatchSize) { first };
                    while (batch.Count < MaxBatchSize && _queue.TryTake(out var more))
                        batch.Add(more);

                    try
                    {
                        DispatchAsync(batch, _shutdownCts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException oce) when (_shutdownCts.IsCancellationRequested)
                    {
                        foreach (var b in batch) FaultItem(b, oce);
                        break;
                    }
                    catch (Exception ex)
                    {
                        foreach (var b in batch) FaultItem(b, ex);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch { /* never let the worker thread crash */ }
        }

        /// <summary>
        /// Acquires a rate-limit slot and POSTs the request, retrying 429/5xx
        /// up to <paramref name="maxAttempts"/> times with adaptive backoff.
        /// The supplied factory must produce a fresh <see cref="HttpRequestMessage"/>
        /// on each call (HttpRequestMessage cannot be reused).
        /// </summary>
        protected async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> requestFactory,
            HttpCompletionOption completionOption,
            CancellationToken ct,
            int maxAttempts = 6)
        {
            var rng = new Random();
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await _limiter.WaitForSlotAsync(ct).ConfigureAwait(false);

                HttpResponseMessage resp = null;
                HttpRequestMessage req = requestFactory();
                try
                {
                    resp = await Http.SendAsync(req, completionOption, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    req.Dispose();
                    _limiter.OnTransientError();
                    var backoffMs = ExponentialBackoffMs(attempt, rng);
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                    continue;
                }
                req.Dispose();

                var status = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    _limiter.OnSuccess();
                    return resp;
                }
                bool retryable = status == 429 || (status >= 500 && status <= 599);
                if (!retryable || attempt >= maxAttempts) return resp;

                if (status == 429)
                    _limiter.OnRateLimited(resp.Headers?.RetryAfter);
                else
                    _limiter.OnTransientError();

                resp.Dispose();
                var waitMs = ExponentialBackoffMs(attempt, rng);
                await Task.Delay(waitMs, ct).ConfigureAwait(false);
            }
        }

        private static int ExponentialBackoffMs(int attempt, Random rng)
        {
            var baseMs = (int)Math.Min(60_000, 1000 * Math.Pow(2, attempt - 1));
            return baseMs + rng.Next(0, 500);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _shutdownCts.Cancel(); } catch { }
            try { _worker.Join(TimeSpan.FromSeconds(2)); } catch { }
            // Drain any leftover items so callers don't await forever.
            while (_queue.TryTake(out var leftover))
            {
                try { FaultItem(leftover, new ObjectDisposedException(_laneName)); } catch { }
            }
            try { _queue.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
        }
    }
}
