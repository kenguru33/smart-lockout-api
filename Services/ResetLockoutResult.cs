namespace SmartLockoutApi.Services;

public abstract record ResetLockoutResult
{
    private ResetLockoutResult() { }

    public sealed record Success(string Upn) : ResetLockoutResult;
    public sealed record NotFound(string Upn) : ResetLockoutResult;
    public sealed record Error(string Message) : ResetLockoutResult;
}
