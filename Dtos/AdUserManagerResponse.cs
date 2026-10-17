namespace SmartLockoutApi.Dtos;

// 200 response for GET /api/ad/user/{upn}/manager.
// Null manager fields mean no manager is set in AD, or the stored manager
// reference could not be resolved to a user. The manager DN is never returned.
public sealed record AdUserManagerResponse(
    string UserPrincipalName,
    string? ManagerUserPrincipalName,
    string? ManagerDisplayName);
