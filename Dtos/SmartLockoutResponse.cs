namespace SmartLockoutApi.Dtos;

public sealed record SmartLockoutResponse(
    string UserPrincipalName,
    bool IsLockedOut,
    bool FamiliarLockout,
    bool UnknownLockout,
    int BadPwdCountFamiliar,
    int BadPwdCountUnknown,
    DateTimeOffset? LastFailedAuthFamiliar,
    DateTimeOffset? LastFailedAuthUnknown,
    IReadOnlyList<string> FamiliarIps);
