using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// IMPORTANT: This implementation can only run on a Windows host where the
// AD FS PowerShell module is installed and importable — i.e. an AD FS server
// itself, or an admin workstation with AD FS RSAT. On any other host the
// Import-Module ADFS call will fail and every request will return Error.
public sealed class PowerShellAdfsSmartLockoutService : IAdfsSmartLockoutService
{
    private readonly ILogger<PowerShellAdfsSmartLockoutService> _logger;
    private readonly InitialSessionState _sessionState;

    public PowerShellAdfsSmartLockoutService(ILogger<PowerShellAdfsSmartLockoutService> logger)
    {
        _logger = logger;
        _sessionState = InitialSessionState.CreateDefault();
        _sessionState.ImportPSModule(new[] { "ADFS" });
    }

    public async Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create(_sessionState);

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
