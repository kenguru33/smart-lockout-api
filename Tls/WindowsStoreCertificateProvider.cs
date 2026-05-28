using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace SmartLockoutApi.Tls;

// Loads and caches the server certificate from the Windows certificate store,
// keyed by subject (CN/SAN DnsName). The initial load happens in the
// constructor and throws on any failure so startup fails fast; subsequent
// Refresh() calls catch and log so a transient store-read error never stops
// the service.
//
// Selection rules (per the spec, all enforced together):
//   - subject (case-insensitive) matches a DnsName SAN entry; if no SAN is
//     present at all, fall back to the CN (per RFC 6125);
//   - currently within [NotBefore, NotAfter];
//   - has the Server Authentication EKU (1.3.6.1.5.5.7.3.1) — LE always sets
//     this; strict mode catches the wrong cert being picked;
//   - has a private key the process account can read.
// Among all that pass, the cert with the latest NotBefore wins. This is what
// makes the renewal-overlap case (two valid LE certs in the store) safe.
public sealed class WindowsStoreCertificateProvider : IServerCertificateProvider, IDisposable
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private readonly CertificateOptions _options;
    private readonly ILogger<WindowsStoreCertificateProvider> _logger;
    private X509Certificate2 _current;

    public WindowsStoreCertificateProvider(
        IOptions<CertificateOptions> options,
        ILogger<WindowsStoreCertificateProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Subject))
        {
            throw new InvalidOperationException(
                $"{CertificateOptions.SectionName}:Subject must be set to enable the Windows certificate-store TLS branch.");
        }

        _current = LoadOrThrow();
        _logger.LogInformation(
            "Loaded TLS certificate {Thumbprint} for subject '{Subject}' (NotAfter {NotAfter:O})",
            _current.Thumbprint, _options.Subject, _current.NotAfter);
    }

    public X509Certificate2 Current => _current;

    public void Refresh()
    {
        X509Certificate2 next;
        try
        {
            next = LoadOrThrow();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TLS certificate refresh failed; keeping previous cert {Thumbprint}", _current.Thumbprint);
            return;
        }

        if (string.Equals(next.Thumbprint, _current.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            next.Dispose();
            return;
        }

        // Atomic swap. The old cert is NOT eagerly disposed — in-flight TLS
        // handshakes may still hold a reference. GC + finalizer reclaim it.
        var old = Interlocked.Exchange(ref _current, next);
        _logger.LogInformation(
            "TLS certificate swapped: old {OldThumbprint} -> new {NewThumbprint}, subject '{Subject}', NotAfter {NotAfter:O}",
            old.Thumbprint, next.Thumbprint, _options.Subject, next.NotAfter);
    }

    public void Dispose()
    {
        _current?.Dispose();
    }

    private X509Certificate2 LoadOrThrow()
    {
        var location = ParseLocation(_options.StoreLocation);
        using var store = new X509Store(_options.StoreName, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        var subject = _options.Subject!;
        var nowUtc = DateTime.UtcNow;

        X509Certificate2? winner = null;
        var rejections = new List<string>();

        foreach (var cert in store.Certificates)
        {
            var reason = EvaluateCandidate(cert, subject, nowUtc);
            if (reason is null)
            {
                if (winner is null || cert.NotBefore > winner.NotBefore)
                {
                    winner?.Dispose();
                    winner = cert;
                }
                else
                {
                    cert.Dispose();
                }
                continue;
            }

            // Only record rejections for subject-matching certs; otherwise the
            // whole store would fill the log.
            if (reason != SubjectMismatchReason)
            {
                rejections.Add($"{cert.Thumbprint} ({reason})");
            }
            cert.Dispose();
        }

        if (winner is not null) return winner;

        var detail = rejections.Count > 0
            ? string.Join("; ", rejections)
            : "no certificate with matching subject";
        throw new InvalidOperationException(
            $"No usable TLS certificate found in {location}/{_options.StoreName} for subject '{subject}': {detail}");
    }

    private const string SubjectMismatchReason = "subject mismatch";

    private static string? EvaluateCandidate(X509Certificate2 cert, string subject, DateTime nowUtc)
    {
        if (!MatchesSubject(cert, subject)) return SubjectMismatchReason;
        if (nowUtc < cert.NotBefore.ToUniversalTime()) return $"not yet valid (NotBefore {cert.NotBefore:O})";
        if (nowUtc > cert.NotAfter.ToUniversalTime()) return $"expired (NotAfter {cert.NotAfter:O})";
        if (!HasServerAuthEku(cert)) return "missing Server Authentication EKU";
        if (!HasUsablePrivateKey(cert)) return "private key not accessible";
        return null;
    }

    private static bool MatchesSubject(X509Certificate2 cert, string subject)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is not null)
        {
            // RFC 6125: when SAN is present, CN is ignored.
            foreach (var dnsName in san.EnumerateDnsNames())
            {
                if (string.Equals(dnsName, subject, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        var cn = cert.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        return string.Equals(cn, subject, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasServerAuthEku(X509Certificate2 cert)
    {
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null) return false;
        foreach (var oid in eku.EnhancedKeyUsages)
        {
            if (oid.Value == ServerAuthenticationOid) return true;
        }
        return false;
    }

    private static bool HasUsablePrivateKey(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey) return false;
        try
        {
            // Probe for an accessible key. Different algorithms expose
            // different accessors; one of these returns non-null when the
            // process can actually read the private key.
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is not null) return true;
            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa is not null) return true;
            using var dsa = cert.GetDSAPrivateKey();
            return dsa is not null;
        }
        catch
        {
            // Access denied / CSP error → not usable.
            return false;
        }
    }

    private static StoreLocation ParseLocation(string value)
    {
        if (Enum.TryParse<StoreLocation>(value, ignoreCase: true, out var location))
        {
            return location;
        }
        throw new InvalidOperationException(
            $"{CertificateOptions.SectionName}:StoreLocation '{value}' is not valid (expected 'LocalMachine' or 'CurrentUser').");
    }
}
