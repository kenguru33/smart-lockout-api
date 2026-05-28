using System.Security.Cryptography.X509Certificates;

namespace SmartLockoutApi.Tls;

// Source of the live server certificate for Kestrel's ServerCertificateSelector.
// Implementations cache the current cert and atomically swap it on Refresh()
// when a newer matching cert appears in the store (e.g., after a win-acme
// renewal). Current is never null after construction — the constructor
// resolves an initial cert and throws if none is usable.
public interface IServerCertificateProvider
{
    X509Certificate2 Current { get; }

    void Refresh();
}
