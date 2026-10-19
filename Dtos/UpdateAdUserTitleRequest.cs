namespace SmartLockoutApi.Dtos;

// Body for PATCH /api/ad/user/{upn}/title. Partial-update semantics:
//   - null / omitted   → leave the attribute unchanged (the only field, so this
//                        is a no-op and is rejected with 400 by the endpoint)
//   - "" (empty string) → clear the attribute in AD
//   - non-empty value   → set the attribute (validated by TitleValidator)
public sealed record UpdateAdUserTitleRequest(
    string? Title);
