using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// Closed sum type for the manager read path. Mirrors AdDepartmentReadResult.
public abstract record AdManagerReadResult
{
    private AdManagerReadResult() { }

    public sealed record Found(AdUserManagerResponse Response) : AdManagerReadResult;
    public sealed record NotFound(string Upn) : AdManagerReadResult;
    public sealed record Error(string Message) : AdManagerReadResult;
}
