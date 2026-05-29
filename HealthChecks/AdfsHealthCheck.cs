using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartLockoutApi.Services;

namespace SmartLockoutApi.HealthChecks;

// Confirms the AD FS server is responding by running Get-AdfsProperties on
// the existing AD FS runspace. Wrapped in a 3 s timeout — a slow AD FS is
// not Healthy. Cached upstream (CachedHealthCheck) so monitors polling at
// high frequency do not multiply load on the real AD FS service.
public sealed class AdfsHealthCheck : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IAdfsSmartLockoutService _service;
    private readonly ILogger<AdfsHealthCheck> _logger;

    public AdfsHealthCheck(IAdfsSmartLockoutService service, ILogger<AdfsHealthCheck> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        try
        {
            await _service.PingAsync(cts.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Get-AdfsProperties succeeded");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AdfsHealthCheck timed out after {Timeout}", ProbeTimeout);
            return HealthCheckResult.Unhealthy($"AD FS probe timed out after {ProbeTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AdfsHealthCheck failed");
            return HealthCheckResult.Unhealthy("AD FS probe failed", ex);
        }
    }
}
