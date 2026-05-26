namespace SmartLockoutApi.Services;

public interface IAdfsSmartLockoutService
{
    Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken);
}
