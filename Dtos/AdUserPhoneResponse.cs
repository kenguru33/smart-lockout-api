namespace SmartLockoutApi.Dtos;

// 200 response for GET /api/ad/user/{upn}/phone.
// A null Mobile/TelephoneNumber means the attribute is unset in AD.
public sealed record AdUserPhoneResponse(
    string UserPrincipalName,
    string? Mobile,
    string? TelephoneNumber);
