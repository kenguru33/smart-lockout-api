namespace SmartLockoutApi.Services;

// Closed sum type for the manager update path. Unlike the other update results
// it separates "target not found" (404) from problems with the referenced
// manager (422), and reports the previous manager for the audit trail.
public abstract record AdManagerUpdateResult
{
    private AdManagerUpdateResult() { }

    // PreviousManagerUpn is null when no manager was set, or the old reference
    // could not be resolved to a user.
    public sealed record Success(string Upn, string? PreviousManagerUpn) : AdManagerUpdateResult;
    public sealed record NotFound(string Upn) : AdManagerUpdateResult;
    public sealed record ManagerNotFound(string ManagerUpn) : AdManagerUpdateResult;
    public sealed record ManagerDisabled(string ManagerUpn) : AdManagerUpdateResult;
    public sealed record SelfManager(string Upn) : AdManagerUpdateResult;
    public sealed record Error(string Message) : AdManagerUpdateResult;
}
