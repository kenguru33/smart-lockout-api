namespace SmartLockoutApi.Dtos;

// 200 response for GET /api/ad/user/{upn}/department.
// A null Department means the attribute is unset in AD.
public sealed record AdUserDepartmentResponse(
    string UserPrincipalName,
    string? Department);
