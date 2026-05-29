using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

public interface IAdUserPhoneService
{
    Task<AdPhoneReadResult> GetPhoneAsync(string upn, CancellationToken cancellationToken);

    Task<AdPhoneUpdateResult> UpdatePhoneAsync(
        string upn,
        UpdateAdUserPhoneRequest request,
        CancellationToken cancellationToken);

    // Health probe: confirms a domain controller is reachable via LDAP.
    // Throws on failure or timeout; returns on success. Used by AdHealthCheck.
    Task PingAsync(CancellationToken cancellationToken);
}
