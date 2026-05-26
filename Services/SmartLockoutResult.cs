using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

public abstract record SmartLockoutResult
{
    private SmartLockoutResult() { }

    public sealed record Found(SmartLockoutResponse Response) : SmartLockoutResult;
    public sealed record NotFound(string Upn) : SmartLockoutResult;
    public sealed record Error(string Message) : SmartLockoutResult;
}
