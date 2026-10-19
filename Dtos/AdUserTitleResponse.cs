namespace SmartLockoutApi.Dtos;

// 200 response for GET /api/ad/user/{upn}/title.
// A null Title means the attribute is unset in AD.
public sealed record AdUserTitleResponse(
    string UserPrincipalName,
    string? Title);
