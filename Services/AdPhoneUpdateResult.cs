namespace SmartLockoutApi.Services;

// Closed sum type for the update path. Mirrors ResetLockoutResult.
public abstract record AdPhoneUpdateResult
{
    private AdPhoneUpdateResult() { }

    public sealed record Success(string Upn) : AdPhoneUpdateResult;
    public sealed record NotFound(string Upn) : AdPhoneUpdateResult;
    public sealed record Error(string Message) : AdPhoneUpdateResult;
}
