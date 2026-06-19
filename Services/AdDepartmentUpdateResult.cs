namespace SmartLockoutApi.Services;

// Closed sum type for the department update path. Mirrors AdPhoneUpdateResult.
public abstract record AdDepartmentUpdateResult
{
    private AdDepartmentUpdateResult() { }

    public sealed record Success(string Upn) : AdDepartmentUpdateResult;
    public sealed record NotFound(string Upn) : AdDepartmentUpdateResult;
    public sealed record Error(string Message) : AdDepartmentUpdateResult;
}
