using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

public interface IAdUserDirectoryService
{
    Task<AdPhoneReadResult> GetPhoneAsync(string upn, CancellationToken cancellationToken);

    Task<AdPhoneUpdateResult> UpdatePhoneAsync(
        string upn,
        UpdateAdUserPhoneRequest request,
        CancellationToken cancellationToken);

    Task<AdDepartmentReadResult> GetDepartmentAsync(string upn, CancellationToken cancellationToken);

    Task<AdDepartmentUpdateResult> UpdateDepartmentAsync(
        string upn,
        UpdateAdUserDepartmentRequest request,
        CancellationToken cancellationToken);

    Task<AdManagerReadResult> GetManagerAsync(string upn, CancellationToken cancellationToken);

    Task<AdManagerUpdateResult> UpdateManagerAsync(
        string upn,
        UpdateAdUserManagerRequest request,
        CancellationToken cancellationToken);

    // Health probe: confirms a domain controller is reachable via LDAP.
    // Throws on failure or timeout; returns on success. Used by AdHealthCheck.
    Task PingAsync(CancellationToken cancellationToken);
}
