using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartLockoutApi.Tls;

namespace SmartLockoutApi.HealthChecks;

// In-process check on the currently-served TLS certificate. The provider's
// background refresher updates Current; we just read it. Three-level result:
//   - expired                 → Unhealthy (shouldn't happen — startup would
//                                have failed — but defends against a refresher
//                                bug or a future code path that allows
//                                serving an expired cert)
//   - NotAfter within 14 days → Degraded (early warning before win-acme's
//                                renewal cadence really matters)
//   - else                    → Healthy
//
// Registered only when the Windows cert-store TLS branch is active (gated in
// Program.cs the same way the provider itself is gated).
public sealed class CertificateHealthCheck : IHealthCheck
{
    private static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    private readonly IServerCertificateProvider _provider;

    public CertificateHealthCheck(IServerCertificateProvider provider)
    {
        _provider = provider;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cert = _provider.Current;
        var now = DateTime.UtcNow;
        var notAfterUtc = cert.NotAfter.ToUniversalTime();

        if (now >= notAfterUtc)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"TLS cert {cert.Thumbprint} expired at {notAfterUtc:O}"));
        }

        var remaining = notAfterUtc - now;
        if (remaining <= ExpiryWarningWindow)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"TLS cert {cert.Thumbprint} expires in {remaining.TotalDays:F1} days ({notAfterUtc:O})"));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"TLS cert {cert.Thumbprint} valid until {notAfterUtc:O}"));
    }
}
