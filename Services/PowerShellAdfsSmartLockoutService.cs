using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// IMPORTANT: This implementation can only run on a Windows host where the
// AD FS PowerShell module is installed and importable — i.e. an AD FS server
// itself, or an admin workstation with AD FS RSAT.
//
// Microsoft.PowerShell.SDK hosts PowerShell 7, but the ADFS module is
// Windows PowerShell 5.1 only, so we import it via -UseWindowsPowerShell.
// That spins up a hidden WinPS 5.1 compat process and creates proxy
// functions in our runspace. The runspace is opened once and reused across
// requests so the compat session and proxies survive for the lifetime of
// the service; pipelines are serialized via a semaphore because a single
// Runspace processes one pipeline at a time.
public sealed class PowerShellAdfsSmartLockoutService : IAdfsSmartLockoutService, IDisposable
{
    private readonly ILogger<PowerShellAdfsSmartLockoutService> _logger;
    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellAdfsSmartLockoutService(ILogger<PowerShellAdfsSmartLockoutService> logger)
    {
        _logger = logger;
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
        _runspace.Open();

        try
        {
            ImportAdfsModule();
        }
        catch
        {
            _runspace.Dispose();
            _gate.Dispose();
            throw;
        }

        _logger.LogInformation("ADFS module loaded via Windows PowerShell compatibility session");
    }

    private void ImportAdfsModule()
    {
        // -UseWindowsPowerShell generates a proxy .psm1 and imports it into
        // this runspace. If the host's execution policy is Restricted/AllSigned
        // that import is refused. Process-scope Bypass affects only this
        // .NET process, requires no admin, and does not change machine policy.
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
          .AddParameter("Name", "ADFS")
          .AddParameter("UseWindowsPowerShell")
          .AddParameter("ErrorAction", "Stop");

        try
        {
            ps.Invoke();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to import the ADFS PowerShell module via -UseWindowsPowerShell. " +
                "Verify the host has the ADFS module installed and the process account has the required rights.",
                ex);
        }

        if (ps.HadErrors)
        {
            var err = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "Unknown error";
            throw new InvalidOperationException(
                $"Failed to import the ADFS PowerShell module via -UseWindowsPowerShell: {err}");
        }
    }

    public async Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            // Pass UPN as a typed parameter — never concatenate into a script string.
            // This is the primary defense against PowerShell injection; UpnValidator
            // is defense-in-depth.
            ps.AddCommand("Get-AdfsAccountActivity").AddParameter("Identity", upn);

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
                _logger.LogError(ex, "PowerShell invocation threw for UPN {Upn}", upn);
                return new SmartLockoutResult.Error($"PowerShell invocation failed: {ex.Message}");
            }

            if (ps.HadErrors)
            {
                var firstError = ps.Streams.Error.FirstOrDefault();
                var category = firstError?.CategoryInfo?.Category;

                if (category == ErrorCategory.ObjectNotFound)
                {
                    _logger.LogInformation("No AD FS account activity for UPN {Upn}", upn);
                    return new SmartLockoutResult.NotFound(upn);
                }

                var message = firstError?.ToString() ?? "Unknown PowerShell error";
                _logger.LogError("PowerShell reported errors for UPN {Upn}: {Message}", upn, message);
                return new SmartLockoutResult.Error(message);
            }

            if (results.Count == 0)
            {
                _logger.LogInformation("Get-AdfsAccountActivity returned no rows for UPN {Upn}", upn);
                return new SmartLockoutResult.NotFound(upn);
            }

            var activity = results[0];
            var response = Project(upn, activity);
            return new SmartLockoutResult.Found(response);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResetLockoutResult> ResetAsync(string upn, CancellationToken cancellationToken)
    {
        // The ADFS cmdlet's -Location is mandatory and (on the Windows PowerShell
        // 5.1 ADFS module) does not accept "Both", so we clear each bucket
        // separately. A NotFound on one bucket is fine if the other cleared —
        // it just means the user wasn't tracked there. Only when BOTH report
        // NotFound do we surface NotFound to the caller.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var familiar = await InvokeResetAsync(upn, "Familiar", cancellationToken).ConfigureAwait(false);
            if (familiar is ResetLockoutResult.Error) return familiar;

            var unknown = await InvokeResetAsync(upn, "Unknown", cancellationToken).ConfigureAwait(false);
            if (unknown is ResetLockoutResult.Error) return unknown;

            if (familiar is ResetLockoutResult.NotFound && unknown is ResetLockoutResult.NotFound)
            {
                return new ResetLockoutResult.NotFound(upn);
            }

            return new ResetLockoutResult.Success(upn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ResetLockoutResult> InvokeResetAsync(string upn, string location, CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;

        // Same trust-boundary rule as GetAsync: typed parameters, never
        // interpolated into a script string. `location` is a hard-coded literal
        // from this file, not caller input.
        ps.AddCommand("Reset-AdfsAccountLockout")
          .AddParameter("Identity", upn)
          .AddParameter("Location", location);

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
            _logger.LogError(ex, "Reset-AdfsAccountLockout threw for UPN {Upn} location {Location}", upn, location);
            return new ResetLockoutResult.Error($"PowerShell invocation failed: {ex.Message}");
        }

        if (ps.HadErrors)
        {
            var firstError = ps.Streams.Error.FirstOrDefault();
            var category = firstError?.CategoryInfo?.Category;

            if (category == ErrorCategory.ObjectNotFound)
            {
                _logger.LogInformation("Reset-AdfsAccountLockout: no record for UPN {Upn} location {Location}", upn, location);
                return new ResetLockoutResult.NotFound(upn);
            }

            var message = firstError?.ToString() ?? "Unknown PowerShell error";
            _logger.LogError("Reset-AdfsAccountLockout reported errors for UPN {Upn} location {Location}: {Message}", upn, location, message);
            return new ResetLockoutResult.Error(message);
        }

        return new ResetLockoutResult.Success(upn);
    }

    public void Dispose()
    {
        _gate.Dispose();
        _runspace.Dispose();
    }

    private static SmartLockoutResponse Project(string upn, PSObject activity)
    {
        var familiarLockout = ReadBool(activity, "FamiliarLockout");
        var unknownLockout = ReadBool(activity, "UnknownLockout");

        return new SmartLockoutResponse(
            UserPrincipalName: ReadString(activity, "Identifier") ?? upn,
            IsLockedOut: familiarLockout || unknownLockout,
            FamiliarLockout: familiarLockout,
            UnknownLockout: unknownLockout,
            BadPwdCountFamiliar: ReadInt(activity, "BadPwdCountFamiliar"),
            BadPwdCountUnknown: ReadInt(activity, "BadPwdCountUnknown"),
            LastFailedAuthFamiliar: ReadDateTimeOffset(activity, "LastFailedAuthFamiliar"),
            LastFailedAuthUnknown: ReadDateTimeOffset(activity, "LastFailedAuthUnknown"),
            FamiliarIps: ReadStringList(activity, "FamiliarIPs"));
    }

    private static object? ReadProperty(PSObject obj, string name) =>
        obj.Properties[name]?.Value;

    private static string? ReadString(PSObject obj, string name) =>
        ReadProperty(obj, name)?.ToString();

    private static bool ReadBool(PSObject obj, string name) =>
        ReadProperty(obj, name) is bool b && b;

    private static int ReadInt(PSObject obj, string name)
    {
        var value = ReadProperty(obj, name);
        return value switch
        {
            null => 0,
            int i => i,
            IConvertible c => Convert.ToInt32(c, CultureInfo.InvariantCulture),
            _ => 0
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(PSObject obj, string name)
    {
        return ReadProperty(obj, name) switch
        {
            null => null,
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt),
            _ => null
        };
    }

    private static IReadOnlyList<string> ReadStringList(PSObject obj, string name)
    {
        var value = ReadProperty(obj, name);
        if (value is null) return Array.Empty<string>();
        if (value is IEnumerable<string> typed) return typed.ToArray();
        if (value is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object?>()
                .Where(o => o is not null)
                .Select(o => o!.ToString()!)
                .ToArray();
        }
        return new[] { value.ToString() ?? string.Empty };
    }
}
