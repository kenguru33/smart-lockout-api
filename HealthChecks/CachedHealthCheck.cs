using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartLockoutApi.HealthChecks;

// Decorator that caches the result of an inner IHealthCheck for a fixed TTL.
// Solves two problems with the PowerShell-based checks in this project:
//
//   1. Monitor pressure on AD / AD FS — if a monitor polls /health every 5 s,
//      the upstream sees one probe per TTL window instead of every 5 s.
//   2. Semaphore contention — the PS services serialise pipelines on a
//      SemaphoreSlim(1,1). Without caching, /health calls would queue
//      behind in-flight real requests and could time out under load.
//
// Generic over TInner so each registered check gets its own singleton with
// its own cache slot — `CachedHealthCheck<AdfsHealthCheck>` and
// `CachedHealthCheck<AdHealthCheck>` are different DI types with separate state.
//
// No stampede control: under concurrent cache-miss requests, the inner check
// may run more than once. Acceptable for this load profile (rare concurrent
// monitor polls) and avoids head-of-line blocking when the inner is slow.
public sealed class CachedHealthCheck<TInner> : IHealthCheck where TInner : IHealthCheck
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly TInner _inner;
    private readonly object _lock = new();
    private CacheEntry? _cached;

    public CachedHealthCheck(TInner inner)
    {
        _inner = inner;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            if (_cached is { } c && (now - c.CachedAt) < CacheTtl)
            {
                return c.Result;
            }
        }

        var result = await _inner.CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            _cached = new CacheEntry(DateTime.UtcNow, result);
        }

        return result;
    }

    private sealed record CacheEntry(DateTime CachedAt, HealthCheckResult Result);
}
