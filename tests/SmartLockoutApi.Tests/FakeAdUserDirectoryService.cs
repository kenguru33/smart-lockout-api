using SmartLockoutApi.Dtos;
using SmartLockoutApi.Services;

namespace SmartLockoutApi.Tests;

// In-memory stand-in for the PowerShell-backed directory service. Holds a few
// synthetic users and mirrors the manager contract of the real service:
// unknown target → NotFound, unknown manager → ManagerNotFound, disabled
// manager → ManagerDisabled, same DN → SelfManager. Title follows the
// department-style contract (unknown target → NotFound, "" → clear). Only the
// manager and title methods are implemented; the others are not exercised.
public sealed class FakeAdUserDirectoryService : IAdUserDirectoryService
{
    public sealed class User(string dn, string upn, string displayName, bool enabled)
    {
        public string Dn { get; } = dn;
        public string Upn { get; } = upn;
        public string DisplayName { get; } = displayName;
        public bool Enabled { get; } = enabled;
        public string? ManagerDn { get; set; }
        public string? Title { get; set; }
    }

    private readonly Dictionary<string, User> _byUpn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, User> _byDn = new(StringComparer.OrdinalIgnoreCase);

    // Number of manager calls that reached the service. Validation failures
    // must leave this at zero.
    public int ManagerCalls { get; private set; }

    // Number of title calls that reached the service.
    public int TitleCalls { get; private set; }

    // When set, every manager and title call returns Error with this message.
    public string? FailWith { get; set; }

    public FakeAdUserDirectoryService Add(User user)
    {
        _byUpn[user.Upn] = user;
        _byDn[user.Dn] = user;
        return this;
    }

    // Maps an extra UPN onto an existing user, to model two UPNs that resolve
    // to the same AD object (exercises the DN-based self check).
    public FakeAdUserDirectoryService AddAlias(string aliasUpn, string upn)
    {
        _byUpn[aliasUpn] = _byUpn[upn];
        return this;
    }

    // The user's current title, read straight from the fake store.
    public string? TitleOf(string upn) => _byUpn[upn].Title;

    // The UPN of the user's current manager, read straight from the fake store.
    public string? ManagerUpnOf(string upn) =>
        _byUpn[upn].ManagerDn is { } dn ? _byDn.GetValueOrDefault(dn)?.Upn : null;

    public Task<AdManagerReadResult> GetManagerAsync(string upn, CancellationToken cancellationToken)
    {
        ManagerCalls++;
        if (FailWith is not null) return Task.FromResult<AdManagerReadResult>(new AdManagerReadResult.Error(FailWith));
        if (!_byUpn.TryGetValue(upn, out var user)) return Task.FromResult<AdManagerReadResult>(new AdManagerReadResult.NotFound(upn));

        var manager = user.ManagerDn is null ? null : _byDn.GetValueOrDefault(user.ManagerDn);
        return Task.FromResult<AdManagerReadResult>(new AdManagerReadResult.Found(
            new AdUserManagerResponse(user.Upn, manager?.Upn, manager?.DisplayName)));
    }

    public Task<AdManagerUpdateResult> UpdateManagerAsync(
        string upn,
        UpdateAdUserManagerRequest request,
        CancellationToken cancellationToken)
    {
        ManagerCalls++;
        if (FailWith is not null) return Result(new AdManagerUpdateResult.Error(FailWith));
        if (!_byUpn.TryGetValue(upn, out var target)) return Result(new AdManagerUpdateResult.NotFound(upn));

        string? newManagerDn = null;
        if (request.Manager!.Length > 0)
        {
            if (!_byUpn.TryGetValue(request.Manager, out var manager)) return Result(new AdManagerUpdateResult.ManagerNotFound(request.Manager));
            if (!manager.Enabled) return Result(new AdManagerUpdateResult.ManagerDisabled(request.Manager));
            if (string.Equals(manager.Dn, target.Dn, StringComparison.OrdinalIgnoreCase)) return Result(new AdManagerUpdateResult.SelfManager(upn));
            newManagerDn = manager.Dn;
        }

        var previous = target.ManagerDn is null ? null : _byDn.GetValueOrDefault(target.ManagerDn)?.Upn;
        target.ManagerDn = newManagerDn;
        return Result(new AdManagerUpdateResult.Success(upn, previous));

        static Task<AdManagerUpdateResult> Result(AdManagerUpdateResult r) => Task.FromResult(r);
    }

    public Task<AdTitleReadResult> GetTitleAsync(string upn, CancellationToken cancellationToken)
    {
        TitleCalls++;
        if (FailWith is not null) return Task.FromResult<AdTitleReadResult>(new AdTitleReadResult.Error(FailWith));
        if (!_byUpn.TryGetValue(upn, out var user)) return Task.FromResult<AdTitleReadResult>(new AdTitleReadResult.NotFound(upn));

        return Task.FromResult<AdTitleReadResult>(new AdTitleReadResult.Found(new AdUserTitleResponse(user.Upn, user.Title)));
    }

    public Task<AdTitleUpdateResult> UpdateTitleAsync(
        string upn,
        UpdateAdUserTitleRequest request,
        CancellationToken cancellationToken)
    {
        TitleCalls++;
        if (FailWith is not null) return Task.FromResult<AdTitleUpdateResult>(new AdTitleUpdateResult.Error(FailWith));
        if (!_byUpn.TryGetValue(upn, out var user)) return Task.FromResult<AdTitleUpdateResult>(new AdTitleUpdateResult.NotFound(upn));

        // "" → clear; non-empty → set. (null is rejected by the endpoint.)
        var previous = user.Title;
        user.Title = request.Title!.Length == 0 ? null : request.Title;
        return Task.FromResult<AdTitleUpdateResult>(new AdTitleUpdateResult.Success(upn, previous));
    }

    public Task<AdPhoneReadResult> GetPhoneAsync(string upn, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AdPhoneUpdateResult> UpdatePhoneAsync(string upn, UpdateAdUserPhoneRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AdDepartmentReadResult> GetDepartmentAsync(string upn, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AdDepartmentUpdateResult> UpdateDepartmentAsync(string upn, UpdateAdUserDepartmentRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
