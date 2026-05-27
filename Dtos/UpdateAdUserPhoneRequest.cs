namespace SmartLockoutApi.Dtos;

// Body for PATCH /api/ad/user/{upn}/phone. Partial-update semantics:
//   - null / omitted   → leave the attribute unchanged
//   - "" (empty string) → clear the attribute in AD
//   - non-empty value   → set the attribute (validated by PhoneNumberValidator)
// Both null/omitted is a no-op and is rejected with 400 by the endpoint.
public sealed record UpdateAdUserPhoneRequest(
    string? Mobile,
    string? TelephoneNumber);
