namespace SmartLockoutApi.Services;

// Closed sum type for the title update path. Like AdManagerUpdateResult, the
// success case carries the previous value for the audit trail.
public abstract record AdTitleUpdateResult
{
    private AdTitleUpdateResult() { }

    // PreviousTitle is null when the attribute was unset.
    public sealed record Success(string Upn, string? PreviousTitle) : AdTitleUpdateResult;
    public sealed record NotFound(string Upn) : AdTitleUpdateResult;
    public sealed record Error(string Message) : AdTitleUpdateResult;
}
