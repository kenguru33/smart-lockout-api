using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

public interface IAdUserPhoneService
{
    Task<AdPhoneReadResult> GetPhoneAsync(string upn, CancellationToken cancellationToken);

    Task<AdPhoneUpdateResult> UpdatePhoneAsync(
        string upn,
        UpdateAdUserPhoneRequest request,
        CancellationToken cancellationToken);
}
