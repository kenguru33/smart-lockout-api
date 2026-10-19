using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// Closed sum type for the title read path. Mirrors AdDepartmentReadResult.
public abstract record AdTitleReadResult
{
    private AdTitleReadResult() { }

    public sealed record Found(AdUserTitleResponse Response) : AdTitleReadResult;
    public sealed record NotFound(string Upn) : AdTitleReadResult;
    public sealed record Error(string Message) : AdTitleReadResult;
}
