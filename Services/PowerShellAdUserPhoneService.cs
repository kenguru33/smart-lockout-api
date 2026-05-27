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
public sealed class PowerShellAdUserPhoneService : IAdUserPhoneService, IDisposable
{
    private readonly ILogger<PowerShellAdUserPhoneService> _logger;
    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellAdUserPhoneService(ILogger<PowerShellAdUserPhoneService> logger)
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
            var (resolved, distinguishedName) = await ResolveDistinguishedNameAsync(upn, cancellationToken)
                .ConfigureAwait(false);

            switch (resolved)
            {
                case AdPhoneUpdateResult.NotFound nf:
                    return nf;
                case AdPhoneUpdateResult.Error err:
                    return err;
            }

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

    // Resolves the UPN to a DistinguishedName. Returns NotFound/Error as an
    // AdPhoneUpdateResult, or (null result, dn) on success. Caller holds the gate.
    private async Task<(AdPhoneUpdateResult? Result, string? DistinguishedName)> ResolveDistinguishedNameAsync(
        string upn, CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddCommand("Get-ADUser").AddParameter("LDAPFilter", BuildUpnLdapFilter(upn));

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
            _logger.LogError(ex, "Get-ADUser (resolve) invocation threw for UPN {Upn}", upn);
            return (new AdPhoneUpdateResult.Error($"PowerShell invocation failed: {ex.Message}"), null);
        }

        if (ps.HadErrors)
        {
            var firstError = ps.Streams.Error.FirstOrDefault();
            if (firstError?.CategoryInfo?.Category == ErrorCategory.ObjectNotFound)
            {
                return (new AdPhoneUpdateResult.NotFound(upn), null);
            }

            var message = firstError?.ToString() ?? "Unknown PowerShell error";
            _logger.LogError("Get-ADUser (resolve) reported errors for UPN {Upn}: {Message}", upn, message);
            return (new AdPhoneUpdateResult.Error(message), null);
        }

        if (results.Count == 0)
        {
            return (new AdPhoneUpdateResult.NotFound(upn), null);
        }

        var dn = ReadString(results[0], "DistinguishedName");
        if (string.IsNullOrEmpty(dn))
        {
            return (new AdPhoneUpdateResult.Error("Resolved AD user had no DistinguishedName."), null);
        }

        return (null, dn);
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
