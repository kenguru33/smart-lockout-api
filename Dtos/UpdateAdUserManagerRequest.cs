namespace SmartLockoutApi.Dtos;

// Body for PATCH /api/ad/user/{upn}/manager. Partial-update semantics:
//   - null / omitted   → leave the attribute unchanged (the only field, so this
//                        is a no-op and is rejected with 400 by the endpoint)
//   - "" (empty string) → clear the manager in AD
//   - non-empty value   → the manager's UPN (validated by UpnValidator)
public sealed record UpdateAdUserManagerRequest(
    string? Manager);
