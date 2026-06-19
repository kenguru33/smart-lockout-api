using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// Closed sum type for the department read path. The service never throws for
// expected outcomes; it returns one of these. Mirrors AdPhoneReadResult.
public abstract record AdDepartmentReadResult
{
    private AdDepartmentReadResult() { }

    public sealed record Found(AdUserDepartmentResponse Response) : AdDepartmentReadResult;
    public sealed record NotFound(string Upn) : AdDepartmentReadResult;
    public sealed record Error(string Message) : AdDepartmentReadResult;
}
