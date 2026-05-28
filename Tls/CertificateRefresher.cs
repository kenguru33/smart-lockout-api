using Microsoft.Extensions.Options;

namespace SmartLockoutApi.Tls;

// Background loop that re-resolves the TLS cert from the store on a fixed
// interval. The provider's Refresh() catches and logs its own errors, so this
// loop only needs to drive the cadence.
public sealed class CertificateRefresher : BackgroundService
{
    private readonly IServerCertificateProvider _provider;
    private readonly TimeSpan _interval;
    private readonly ILogger<CertificateRefresher> _logger;

    public CertificateRefresher(
        IServerCertificateProvider provider,
        IOptions<CertificateOptions> options,
        ILogger<CertificateRefresher> logger)
    {
        _provider = provider;
        _logger = logger;
        _interval = options.Value.RefreshInterval > TimeSpan.Zero
            ? options.Value.RefreshInterval
            : TimeSpan.FromMinutes(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TLS certificate refresher running every {Interval}", _interval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                _provider.Refresh();
                _logger.LogDebug("TLS certificate refresh poll completed");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
