namespace SmartLockoutApi.Tls;

// Bound from the Kestrel:Certificate configuration section. Subject is the only
// required field; the rest carry defaults that match the spec.
public sealed class CertificateOptions
{
    public const string SectionName = "Kestrel:Certificate";

    // CN or SAN DnsName that the cert must carry. Required; the Windows
    // cert-store TLS branch is only enabled when this is non-empty.
    public string? Subject { get; set; }

    // Defaults to "My" (Personal). Free-form string so non-standard store
    // names are usable; X509Store takes the string overload.
    public string StoreName { get; set; } = "My";

    // "LocalMachine" or "CurrentUser". LocalMachine is the only sensible
    // choice for a Windows service.
    public string StoreLocation { get; set; } = "LocalMachine";

    // How often the background refresher re-resolves from the store. LE
    // renews on the order of every 60 days; this cadence picks up the new
    // cert within minutes without burdening the store.
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
}
