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
