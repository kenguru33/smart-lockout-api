namespace SmartLockoutApi.Services;

public interface IAdfsSmartLockoutService
{
    Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken);

    Task<ResetLockoutResult> ResetAsync(string upn, CancellationToken cancellationToken);
}
