namespace SmartLockoutApi.Services;

public interface IAdfsSmartLockoutService
{
    Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken);

    Task<ResetLockoutResult> ResetAsync(string upn, CancellationToken cancellationToken);

    // Health probe: confirms the AD FS server is responding. Throws on
    // failure or timeout; returns on success. Used by AdfsHealthCheck.
    Task PingAsync(CancellationToken cancellationToken);
}
