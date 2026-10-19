using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// IMPORTANT: like PowerShellAdfsSmartLockoutService, this can only run on a
// Windows host where the relevant module is importable — here the
// ActiveDirectory module (a Domain Controller, or an admin host with the AD
// RSAT tools). It keeps its own runspace, separate from the AD FS service,
// so each service stays single-responsibility.
//
// The ActiveDirectory module is a Windows PowerShell 5.1 module, so it is
// imported via -UseWindowsPowerShell into a long-lived runspace exactly as the
// ADFS module is. Pipelines are serialized via a semaphore because a single
// Runspace processes one pipeline at a time.
//
// TRUST BOUNDARY: Get-ADUser/Set-ADUser do NOT accept a UPN as -Identity, so
// the user is resolved with -LDAPFilter "(userPrincipalName=<value>)". An LDAP
// filter treats the value as DATA, not as a PowerShell expression, so there is
// no script to inject into; the value is additionally LDAP-escaped (RFC 4515)
// as defence-in-depth on top of UpnValidator. (A PowerShell -Filter with a
// $variable does not work here: the ActiveDirectory module runs in the hidden
// Windows PowerShell 5.1 compat session via -UseWindowsPowerShell, and the
// filter expression is evaluated in that session where the variable is unset.)
public sealed class PowerShellAdUserDirectoryService : IAdUserDirectoryService, IDisposable
{
    private readonly ILogger<PowerShellAdUserDirectoryService> _logger;
    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellAdUserDirectoryService(ILogger<PowerShellAdUserDirectoryService> logger)
    {
        _logger = logger;
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
        _runspace.Open();

        try
        {
            ImportActiveDirectoryModule();
        }
        catch
        {
            _runspace.Dispose();
            _gate.Dispose();
            throw;
        }

        _logger.LogInformation("ActiveDirectory module loaded via Windows PowerShell compatibility session");
    }

    private void ImportActiveDirectoryModule()
    {
        // Process-scope Bypass so the -UseWindowsPowerShell proxy import is not
        // refused under a Restricted/AllSigned host policy. Affects only this
        // .NET process; requires no admin and does not change machine policy.
        using (var setPolicy = PowerShell.Create())
        {
            setPolicy.Runspace = _runspace;
            setPolicy.AddCommand("Set-ExecutionPolicy")
              .AddParameter("Scope", "Process")
              .AddParameter("ExecutionPolicy", "Bypass")
              .AddParameter("Force");
            setPolicy.Invoke();
            if (setPolicy.HadErrors)
            {
                var err = setPolicy.Streams.Error.FirstOrDefault()?.ToString() ?? "Unknown error";
                throw new InvalidOperationException(
                    $"Failed to set process-scope execution policy to Bypass: {err}");
            }
        }

        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("UseWindowsPowerShell")
          .AddParameter("ErrorAction", "Stop");

        try
        {
            ps.Invoke();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to import the ActiveDirectory PowerShell module via -UseWindowsPowerShell. " +
                "Verify the host has the AD RSAT module installed and the process account has the required rights.",
                ex);
        }

        if (ps.HadErrors)
        {
            var err = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "Unknown error";
            throw new InvalidOperationException(
                $"Failed to import the ActiveDirectory PowerShell module via -UseWindowsPowerShell: {err}");
        }
    }

    public async Task<AdPhoneReadResult> GetPhoneAsync(string upn, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            ps.AddCommand("Get-ADUser")
              .AddParameter("LDAPFilter", BuildUpnLdapFilter(upn))
              .AddParameter("Properties", new[] { "MobilePhone", "OfficePhone" });

            PSDataCollection<PSObject> results;
            try
            {
                results = await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get-ADUser invocation threw for UPN {Upn}", upn);
                return new AdPhoneReadResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
                {
                    _logger.LogInformation("No AD user for UPN {Upn}", upn);
                    return new AdPhoneReadResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("Get-ADUser reported errors for UPN {Upn}: {Message}", upn, message);
                return new AdPhoneReadResult.Error(message);
            }

            if (results.Count == 0)
            {
                _logger.LogInformation("Get-ADUser returned no rows for UPN {Upn}", upn);
                return new AdPhoneReadResult.NotFound(upn);
            }

            var user = results[0];
            var response = new AdUserPhoneResponse(
                UserPrincipalName: ReadString(user, "UserPrincipalName") ?? upn,
                Mobile: ReadString(user, "MobilePhone"),
                TelephoneNumber: ReadString(user, "OfficePhone"));
            return new AdPhoneReadResult.Found(response);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdPhoneUpdateResult> UpdatePhoneAsync(
        string upn,
        UpdateAdUserPhoneRequest request,
        CancellationToken cancellationToken)
    {
        // Two gated steps, mirroring ResetAsync: (1) resolve the user so an
        // absent UPN surfaces as NotFound, then (2) apply the change. Non-empty
        // values are assumed already validated/normalized by the endpoint.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (notFound, resolveError, distinguishedName) =
                await ResolveDistinguishedNameAsync(upn, cancellationToken).ConfigureAwait(false);

            if (notFound) return new AdPhoneUpdateResult.NotFound(upn);
            if (resolveError is not null) return new AdPhoneUpdateResult.Error(resolveError);

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // DistinguishedName is bound as a typed parameter, never interpolated.
            var set = ps.AddCommand("Set-ADUser").AddParameter("Identity", distinguishedName);

            // null → skip; "" → clear; non-empty → set.
            var clear = new List<string>();
            if (request.Mobile is not null)
            {
                if (request.Mobile.Length == 0) clear.Add("mobile");
                else set.AddParameter("MobilePhone", request.Mobile);
            }
            if (request.TelephoneNumber is not null)
            {
                if (request.TelephoneNumber.Length == 0) clear.Add("telephoneNumber");
                else set.AddParameter("OfficePhone", request.TelephoneNumber);
            }
            if (clear.Count > 0) set.AddParameter("Clear", clear.ToArray());

            try
            {
                await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser invocation threw for UPN {Upn}", upn);
                return new AdPhoneUpdateResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
                {
                    return new AdPhoneUpdateResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("Set-ADUser reported errors for UPN {Upn}: {Message}", upn, message);
                return new AdPhoneUpdateResult.Error(message);
            }

            return new AdPhoneUpdateResult.Success(upn);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdDepartmentReadResult> GetDepartmentAsync(string upn, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            ps.AddCommand("Get-ADUser")
              .AddParameter("LDAPFilter", BuildUpnLdapFilter(upn))
              .AddParameter("Properties", new[] { "Department" });

            PSDataCollection<PSObject> results;
            try
            {
                results = await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get-ADUser invocation threw for UPN {Upn}", upn);
                return new AdDepartmentReadResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
                {
                    _logger.LogInformation("No AD user for UPN {Upn}", upn);
                    return new AdDepartmentReadResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("Get-ADUser reported errors for UPN {Upn}: {Message}", upn, message);
                return new AdDepartmentReadResult.Error(message);
            }

            if (results.Count == 0)
            {
                _logger.LogInformation("Get-ADUser returned no rows for UPN {Upn}", upn);
                return new AdDepartmentReadResult.NotFound(upn);
            }

            var user = results[0];
            var response = new AdUserDepartmentResponse(
                UserPrincipalName: ReadString(user, "UserPrincipalName") ?? upn,
                Department: ReadString(user, "Department"));
            return new AdDepartmentReadResult.Found(response);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdDepartmentUpdateResult> UpdateDepartmentAsync(
        string upn,
        UpdateAdUserDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        // Two gated steps, mirroring UpdatePhoneAsync: (1) resolve the user so an
        // absent UPN surfaces as NotFound, then (2) apply the change. A non-empty
        // value is assumed already validated/normalized by the endpoint.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (notFound, resolveError, distinguishedName) =
                await ResolveDistinguishedNameAsync(upn, cancellationToken).ConfigureAwait(false);

            if (notFound) return new AdDepartmentUpdateResult.NotFound(upn);
            if (resolveError is not null) return new AdDepartmentUpdateResult.Error(resolveError);

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // DistinguishedName is bound as a typed parameter, never interpolated.
            var set = ps.AddCommand("Set-ADUser").AddParameter("Identity", distinguishedName);

            // "" → clear; non-empty → set. (null is rejected by the endpoint.)
            if (request.Department!.Length == 0) set.AddParameter("Clear", new[] { "department" });
            else set.AddParameter("Department", request.Department);

            try
            {
                await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser invocation threw for UPN {Upn}", upn);
                return new AdDepartmentUpdateResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
                {
                    return new AdDepartmentUpdateResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("Set-ADUser reported errors for UPN {Upn}: {Message}", upn, message);
                return new AdDepartmentUpdateResult.Error(message);
            }

            return new AdDepartmentUpdateResult.Success(upn);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdManagerReadResult> GetManagerAsync(string upn, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (notFound, error, user) = await FindUserAsync(
                "LDAPFilter", BuildUpnLdapFilter(upn), new[] { "Manager" }, upn, cancellationToken)
                .ConfigureAwait(false);

            if (notFound) return new AdManagerReadResult.NotFound(upn);
            if (error is not null) return new AdManagerReadResult.Error(error);

            var targetUpn = ReadString(user!, "UserPrincipalName") ?? upn;
            var managerDn = ReadString(user!, "Manager");
            if (managerDn is null)
            {
                return new AdManagerReadResult.Found(new AdUserManagerResponse(targetUpn, null, null));
            }

            var (managerError, manager) =
                await ResolveUserByDnAsync(managerDn, upn, cancellationToken).ConfigureAwait(false);
            if (managerError is not null) return new AdManagerReadResult.Error(managerError);

            return new AdManagerReadResult.Found(new AdUserManagerResponse(
                UserPrincipalName: targetUpn,
                ManagerUserPrincipalName: manager?.Upn,
                ManagerDisplayName: manager?.DisplayName));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdManagerUpdateResult> UpdateManagerAsync(
        string upn,
        UpdateAdUserManagerRequest request,
        CancellationToken cancellationToken)
    {
        // Gated steps: (1) resolve the target and its current manager, (2) resolve
        // and vet the new manager, (3) resolve the previous manager's UPN for the
        // audit trail, (4) apply. A non-empty manager UPN is assumed already
        // validated/normalized by the endpoint.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (notFound, error, target) = await FindUserAsync(
                "LDAPFilter", BuildUpnLdapFilter(upn), new[] { "Manager" }, upn, cancellationToken)
                .ConfigureAwait(false);

            if (notFound) return new AdManagerUpdateResult.NotFound(upn);
            if (error is not null) return new AdManagerUpdateResult.Error(error);

            var targetDn = ReadString(target!, "DistinguishedName");
            if (targetDn is null) return new AdManagerUpdateResult.Error("Resolved AD user had no DistinguishedName.");

            // "" → clear; non-empty → set. (null is rejected by the endpoint.)
            var managerUpn = request.Manager!;
            string? managerDn = null;
            if (managerUpn.Length > 0)
            {
                var (managerNotFound, managerError, manager) = await FindUserAsync(
                    "LDAPFilter", BuildUpnLdapFilter(managerUpn), Array.Empty<string>(), managerUpn, cancellationToken)
                    .ConfigureAwait(false);

                if (managerNotFound) return new AdManagerUpdateResult.ManagerNotFound(managerUpn);
                if (managerError is not null) return new AdManagerUpdateResult.Error(managerError);

                if (manager!.Properties["Enabled"]?.Value is not true)
                {
                    return new AdManagerUpdateResult.ManagerDisabled(managerUpn);
                }

                managerDn = ReadString(manager, "DistinguishedName");
                if (managerDn is null) return new AdManagerUpdateResult.Error("Resolved manager had no DistinguishedName.");

                // Compare resolved DNs so a UPN alias can't bypass the endpoint's check.
                if (string.Equals(managerDn, targetDn, StringComparison.OrdinalIgnoreCase))
                {
                    return new AdManagerUpdateResult.SelfManager(upn);
                }
            }

            // Best effort: an unresolvable previous manager is recorded as null
            // rather than blocking the update.
            string? previousUpn = null;
            var previousDn = ReadString(target!, "Manager");
            if (previousDn is not null)
            {
                var (_, previous) =
                    await ResolveUserByDnAsync(previousDn, upn, cancellationToken).ConfigureAwait(false);
                previousUpn = previous?.Upn;
            }

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // Both DNs come from AD lookups and are bound as typed parameters.
            var set = ps.AddCommand("Set-ADUser").AddParameter("Identity", targetDn);
            if (managerDn is null) set.AddParameter("Clear", new[] { "manager" });
            else set.AddParameter("Manager", managerDn);

            try
            {
                await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser invocation threw for UPN {Upn}", upn);
                return new AdManagerUpdateResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
                {
                    return new AdManagerUpdateResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("Set-ADUser reported errors for UPN {Upn}: {Message}", upn, message);
                return new AdManagerUpdateResult.Error(message);
            }

            return new AdManagerUpdateResult.Success(upn, previousUpn);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdTitleReadResult> GetTitleAsync(string upn, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (notFound, error, user) = await FindUserAsync(
                "LDAPFilter", BuildUpnLdapFilter(upn), new[] { "Title" }, upn, cancellationToken)
                .ConfigureAwait(false);

            if (notFound) return new AdTitleReadResult.NotFound(upn);
            if (error is not null) return new AdTitleReadResult.Error(error);

            return new AdTitleReadResult.Found(new AdUserTitleResponse(
                UserPrincipalName: ReadString(user!, "UserPrincipalName") ?? upn,
                Title: ReadString(user!, "Title")));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdTitleUpdateResult> UpdateTitleAsync(
        string upn,
        UpdateAdUserTitleRequest request,
        CancellationToken cancellationToken)
    {
        // Two gated steps: (1) resolve the user and its current title (for the
        // audit trail), then (2) apply the change. A non-empty value is assumed
        // already validated/normalized by the endpoint.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (notFound, error, user) = await FindUserAsync(
                "LDAPFilter", BuildUpnLdapFilter(upn), new[] { "Title" }, upn, cancellationToken)
                .ConfigureAwait(false);

            if (notFound) return new AdTitleUpdateResult.NotFound(upn);
            if (error is not null) return new AdTitleUpdateResult.Error(error);

            var distinguishedName = ReadString(user!, "DistinguishedName");
            if (distinguishedName is null) return new AdTitleUpdateResult.Error("Resolved AD user had no DistinguishedName.");
            var previousTitle = ReadString(user!, "Title");

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // DistinguishedName is bound as a typed parameter, never interpolated.
            var set = ps.AddCommand("Set-ADUser").AddParameter("Identity", distinguishedName);

            // "" → clear; non-empty → set. (null is rejected by the endpoint.)
            if (request.Title!.Length == 0) set.AddParameter("Clear", new[] { "title" });
            else set.AddParameter("Title", request.Title);

            try
            {
                await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser invocation threw for UPN {Upn}", upn);
                return new AdTitleUpdateResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
                {
                    return new AdTitleUpdateResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("Set-ADUser reported errors for UPN {Upn}: {Message}", upn, message);
                return new AdTitleUpdateResult.Error(message);
            }

            return new AdTitleUpdateResult.Success(upn, previousTitle);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Resolves the UPN to a DistinguishedName. Returns NotFound=true when the
    // user is absent, a non-null Error message on failure, or a non-null Dn on
    // success — attribute-agnostic so every Update*Async can map it into its own
    // result type. Caller holds the gate.
    private async Task<(bool NotFound, string? Error, string? Dn)> ResolveDistinguishedNameAsync(
        string upn, CancellationToken cancellationToken)
    {
        var (notFound, error, user) = await FindUserAsync(
            "LDAPFilter", BuildUpnLdapFilter(upn), Array.Empty<string>(), upn, cancellationToken)
            .ConfigureAwait(false);

        if (notFound || error is not null) return (notFound, error, null);

        var dn = ReadString(user!, "DistinguishedName");
        if (string.IsNullOrEmpty(dn))
        {
            return (false, "Resolved AD user had no DistinguishedName.", null);
        }

        return (false, null, dn);
    }

    // Resolves a manager DN stored in AD to the manager's UPN and display name.
    // Returns a null user (no error) when the DN is not a user object or no longer
    // exists — a dangling reference or a contact — and logs a Warning without the
    // DN. Caller holds the gate.
    private async Task<(string? Error, (string Upn, string? DisplayName)? User)> ResolveUserByDnAsync(
        string dn, string targetUpn, CancellationToken cancellationToken)
    {
        var (notFound, error, user) = await FindUserAsync(
            "Identity", dn, new[] { "DisplayName" }, targetUpn, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null) return (error, null);

        var managerUpn = notFound ? null : ReadString(user!, "UserPrincipalName");
        if (managerUpn is null)
        {
            _logger.LogWarning("Manager reference on AD user {Upn} does not resolve to a user with a UPN", targetUpn);
            return (null, null);
        }

        return (null, (managerUpn, ReadString(user!, "DisplayName")));
    }

    // Runs Get-ADUser with one lookup parameter (LDAPFilter or Identity), both
    // bound as typed parameters. Returns NotFound=true when the user is absent,
    // a non-null Error message on failure, or the first user on success.
    // Caller holds the gate.
    private async Task<(bool NotFound, string? Error, PSObject? User)> FindUserAsync(
        string lookupParameter,
        string lookupValue,
        string[] properties,
        string upnForLogs,
        CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        var get = ps.AddCommand("Get-ADUser").AddParameter(lookupParameter, lookupValue);
        if (properties.Length > 0) get.AddParameter("Properties", properties);

        PSDataCollection<PSObject> results;
        try
        {
            results = await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get-ADUser (resolve) invocation threw for UPN {Upn}", upnForLogs);
            return (false, $"PowerShell invocation failed: {ex.Message}", null);
        }

        if (ps.HadErrors)
        {
            var firstError = ps.Streams.Error.FirstOrDefault();
            if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
            {
                return (true, null, null);
            }

            var message = firstError?.ToString() ?? "Unknown PowerShell error";
            _logger.LogError("Get-ADUser (resolve) reported errors for UPN {Upn}: {Message}", upnForLogs, message);
            return (false, message, null);
        }

        if (results.Count == 0)
        {
            return (true, null, null);
        }

        return (false, null, results[0]);
    }

    // Cheapest LDAP probe — Get-ADRootDSE returns the directory root entry
    // without touching any user object. Confirms a DC is reachable. Throws on
    // failure so the calling IHealthCheck can map that to Unhealthy.
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-ADRootDSE");

            await ps.InvokeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            if (ps.HadErrors)
            {
                var message = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "Unknown PowerShell error";
                throw new InvalidOperationException($"Get-ADRootDSE reported errors: {message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _runspace.Dispose();
    }

    // AD returns null for unset string attributes; treat blank as unset too.
    private static string? ReadString(PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value?.ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string BuildUpnLdapFilter(string upn) =>
        $"(userPrincipalName={EscapeLdapFilterValue(upn)})";

    // RFC 4515 §3 escaping for an LDAP filter assertion value. UpnValidator
    // already forbids these metacharacters; this is defence-in-depth so the
    // value can never be read as filter syntax. Backslash must be escaped first.
    private static string EscapeLdapFilterValue(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
