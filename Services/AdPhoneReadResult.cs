using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// Closed sum type for the read path. The service never throws for expected
// outcomes; it returns one of these. Mirrors SmartLockoutResult.
public abstract record AdPhoneReadResult
{
    private AdPhoneReadResult() { }

    public sealed record Found(AdUserPhoneResponse Response) : AdPhoneReadResult;
    public sealed record NotFound(string Upn) : AdPhoneReadResult;
    public sealed record Error(string Message) : AdPhoneReadResult;
}
