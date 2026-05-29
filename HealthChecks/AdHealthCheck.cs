using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartLockoutApi.Services;

namespace SmartLockoutApi.HealthChecks;

// Confirms a domain controller is reachable by running Get-ADRootDSE on the
// existing AD runspace. Wrapped in a 3 s timeout. Cached upstream.
public sealed class AdHealthCheck : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IAdUserPhoneService _service;
    private readonly ILogger<AdHealthCheck> _logger;

    public AdHealthCheck(IAdUserPhoneService service, ILogger<AdHealthCheck> logger)
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
            return HealthCheckResult.Healthy("Get-ADRootDSE succeeded");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AdHealthCheck timed out after {Timeout}", ProbeTimeout);
            return HealthCheckResult.Unhealthy($"AD probe timed out after {ProbeTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AdHealthCheck failed");
            return HealthCheckResult.Unhealthy("AD probe failed", ex);
        }
    }
}
