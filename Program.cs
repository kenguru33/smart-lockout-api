// SmartLockoutApi — internal AD FS Extranet Smart Lockout query API.
//
// RUNTIME: the process must run on a Windows host where the "ADFS" PowerShell
// module is available (an AD FS server, or an admin host with AD FS RSAT).
//
// Auth: X-API-Key header, validated by ApiKeyEndpointFilter against
// ApiKey:Keys from configuration. TLS is the deployer's responsibility.

using System.Security.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using SmartLockoutApi.HealthChecks;
using SmartLockoutApi.Services;
using SmartLockoutApi.Tls;
using SmartLockoutApi.Validation;

var builder = WebApplication.CreateBuilder(args);

// Host as a Windows Service when running under the SCM. Without this the
// process starts but never reports SERVICE_RUNNING, so the SCM kills it
// after 30 s (Event Viewer: System log, source SCM, EventID 7000 / 7009).
//
// UseWindowsService is a no-op when not actually running as a service
// (interactive dotnet run, Linux, etc.) — IsWindowsService() is checked
// internally — so it is safe to register unconditionally on Windows. It
// also registers the EventLog provider for us, using ServiceName as the
// source, which replaces the explicit AddEventLog() call we had before.
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "SmartLockoutApi";
    });
}

// HTTPS from the Windows certificate store, with live reload on win-acme /
// Let's Encrypt renewal. Active only when the host is Windows AND a Subject
// is configured — on Linux dev boxes (no Windows store) Kestrel falls back to
// its launchSettings-driven HTTP binding so `dotnet run` still works.
builder.Services.Configure<CertificateOptions>(
    builder.Configuration.GetSection(CertificateOptions.SectionName));

var certOptions = builder.Configuration
    .GetSection(CertificateOptions.SectionName)
    .Get<CertificateOptions>() ?? new CertificateOptions();
var useWindowsCertStoreTls = OperatingSystem.IsWindows()
    && !string.IsNullOrWhiteSpace(certOptions.Subject);

if (useWindowsCertStoreTls)
{
    builder.Services.AddSingleton<IServerCertificateProvider, WindowsStoreCertificateProvider>();
    builder.Services.AddHostedService<CertificateRefresher>();

    // Bind HTTPS explicitly on 5199. The ServerCertificateSelector is called
    // per TLS handshake, returning whatever cert the provider currently has
    // cached — this is what makes the live swap on renewal work.
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(5199, listen =>
        {
            listen.UseHttps(https =>
            {
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                https.ServerCertificateSelector = (_, _) =>
                    options.ApplicationServices
                        .GetRequiredService<IServerCertificateProvider>()
                        .Current;
            });
        });
    });
}

// The PowerShell singleton opens a long-lived Runspace at construction and
// imports the ADFS module via -UseWindowsPowerShell into it; each request
// gets a fresh PowerShell instance attached to that runspace inside GetAsync.
builder.Services.AddSingleton<IAdfsSmartLockoutService, PowerShellAdfsSmartLockoutService>();
// Separate runspace/service importing the ActiveDirectory module, used for the
// AD mobile/telephone and department read+update endpoints. Same
// singleton-with-cached-runspace rationale as the AD FS service.
builder.Services.AddSingleton<IAdUserDirectoryService, PowerShellAdUserDirectoryService>();
builder.Services.AddSingleton<ApiKeyEndpointFilter>();

// /health runs three real readiness checks: AD FS reachability (Get-AdfsProperties),
// AD reachability (Get-ADRootDSE), and TLS-cert freshness (in-process). Each
// PowerShell-based check has a 3 s timeout and all three are wrapped in a 30 s
// cache (CachedHealthCheck<TInner>) to avoid pounding AD/AD FS at monitor
// polling rates and to avoid queueing on the runspace gates behind real requests.
// The cert check is registered only when the cert-store TLS branch is active.
builder.Services.AddSingleton<AdfsHealthCheck>();
builder.Services.AddSingleton<AdHealthCheck>();
builder.Services.AddSingleton<CachedHealthCheck<AdfsHealthCheck>>();
builder.Services.AddSingleton<CachedHealthCheck<AdHealthCheck>>();

var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<CachedHealthCheck<AdfsHealthCheck>>("adfs", tags: new[] { "ready" })
    .AddCheck<CachedHealthCheck<AdHealthCheck>>("ad", tags: new[] { "ready" });

if (useWindowsCertStoreTls)
{
    builder.Services.AddSingleton<CertificateHealthCheck>();
    builder.Services.AddSingleton<CachedHealthCheck<CertificateHealthCheck>>();
    healthChecks.AddCheck<CachedHealthCheck<CertificateHealthCheck>>("cert", tags: new[] { "ready" });
}

// Swagger / OpenAPI is config-driven: on when Swagger:Enabled=true, off
// otherwise. Default is off (GetValue<bool> returns false for a missing key).
// appsettings.Development.json sets it to true so the dev loop is unchanged.
var swaggerEnabled = builder.Configuration.GetValue<bool>("Swagger:Enabled");

if (swaggerEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Name = ApiKeyEndpointFilter.HeaderName,
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Description = "API key. Dev value: dev-only-do-not-use-in-prod"
        });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            }] = Array.Empty<string>()
        });
    });
}

var app = builder.Build();

// Force the PowerShell services to construct now so a module-import failure
// stops startup instead of becoming a 500 on the first request.
app.Services.GetRequiredService<IAdfsSmartLockoutService>();
app.Services.GetRequiredService<IAdUserDirectoryService>();

// Same idea for the TLS cert provider: resolve it now so a missing / expired /
// no-private-key cert stops startup instead of failing the first TLS handshake.
if (useWindowsCertStoreTls)
{
    app.Services.GetRequiredService<IServerCertificateProvider>();
}

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Logger.LogInformation("Swagger UI {State}", swaggerEnabled ? "enabled" : "disabled");

var configuredKeys = builder.Configuration.GetSection("ApiKey:Keys").Get<string[]>() ?? Array.Empty<string>();
if (!configuredKeys.Any(k => !string.IsNullOrWhiteSpace(k)))
{
    app.Logger.LogWarning(
        "ApiKey:Keys is empty; every request will be rejected with 401. " +
        "Set ApiKey__Keys__0 (and __1 during rotation) before deploying.");
}

// Liveness probe. Deliberately NOT gated by ApiKeyEndpointFilter — monitors
// and the SCM should not need the API key. Body: "Healthy" / "Unhealthy".
// (MapHealthChecks returns IEndpointConventionBuilder, which does not carry
// .Produces() metadata — Swagger visibility is best-effort.)
app.MapHealthChecks("/health").WithName("Health");

app.MapGet("/api/adfs/smart-lockout/{upn}", async (
    string upn,
    IAdfsSmartLockoutService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    logger.LogInformation("Querying AD FS smart lockout for UPN {Upn}", normalized);

    var result = await service.GetAsync(normalized, cancellationToken);

    return result switch
    {
        SmartLockoutResult.Found f => Results.Ok(f.Response),
        SmartLockoutResult.NotFound nf => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "No AD FS account activity",
            detail: $"No Get-AdfsAccountActivity record for '{nf.Upn}'."),
        SmartLockoutResult.Error err => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "AD FS PowerShell call failed",
            detail: err.Message),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("GetAdfsSmartLockout")
.Produces<SmartLockoutApi.Dtos.SmartLockoutResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// State-changing endpoint. The shared API key is the only thing gating this,
// so every attempt — accepted or not — is logged at Information with caller
// IP and target UPN so unlocks remain auditable after the fact.
app.MapPost("/api/adfs/smart-lockout/{upn}/reset", async (
    string upn,
    HttpContext http,
    IAdfsSmartLockoutService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on reset");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    var caller = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    logger.LogInformation("AUDIT reset-lockout request: caller {Caller} target {Upn}", caller, normalized);

    var result = await service.ResetAsync(normalized, cancellationToken);

    switch (result)
    {
        case ResetLockoutResult.Success:
            logger.LogInformation("AUDIT reset-lockout success: caller {Caller} target {Upn}", caller, normalized);
            return Results.NoContent();
        case ResetLockoutResult.NotFound nf:
            logger.LogInformation("AUDIT reset-lockout not-found: caller {Caller} target {Upn}", caller, normalized);
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "No AD FS account activity",
                detail: $"No Get-AdfsAccountActivity record for '{nf.Upn}'.");
        case ResetLockoutResult.Error err:
            logger.LogWarning("AUDIT reset-lockout failed: caller {Caller} target {Upn} error {Error}", caller, normalized, err.Message);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "AD FS PowerShell call failed",
                detail: err.Message);
        default:
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
    }
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("ResetAdfsSmartLockout")
.Produces(StatusCodes.Status204NoContent)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// Read the AD mobile/telephone numbers for a UPN. Read-only — no AUDIT line.
app.MapGet("/api/ad/user/{upn}/phone", async (
    string upn,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on phone read");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    logger.LogInformation("Reading AD phone numbers for UPN {Upn}", normalized);

    var result = await service.GetPhoneAsync(normalized, cancellationToken);

    return result switch
    {
        AdPhoneReadResult.Found f => Results.Ok(f.Response),
        AdPhoneReadResult.NotFound nf => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "AD user not found",
            detail: $"No Active Directory user with UPN '{nf.Upn}'."),
        AdPhoneReadResult.Error err => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Active Directory PowerShell call failed",
            detail: err.Message),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("GetAdUserPhone")
.Produces<SmartLockoutApi.Dtos.AdUserPhoneResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// State-changing endpoint, gated only by the shared API key, so every attempt
// is logged at Information with caller IP, target UPN, and which fields changed
// (field names only, never the values). Search logs for `AUDIT update-ad-phone`.
app.MapPatch("/api/ad/user/{upn}/phone", async (
    string upn,
    SmartLockoutApi.Dtos.UpdateAdUserPhoneRequest? request,
    HttpContext http,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on phone update");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    // null/omitted → leave unchanged; "" → clear; non-empty → set (validated).
    var mobile = request?.Mobile;
    var telephone = request?.TelephoneNumber;

    if (mobile is null && telephone is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Nothing to update",
            detail: "Provide at least one of 'mobile' or 'telephoneNumber'. Use \"\" to clear a value.");
    }

    if (mobile is { Length: > 0 })
    {
        if (!PhoneNumberValidator.TryValidate(mobile, out var normMobile))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid phone number",
                detail: "'mobile' must be a Norwegian number: 8 digits (first digit 2-9), optionally prefixed with +47 or 0047.");
        }
        mobile = normMobile;
    }

    if (telephone is { Length: > 0 })
    {
        if (!PhoneNumberValidator.TryValidate(telephone, out var normTelephone))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid phone number",
                detail: "'telephoneNumber' must be a Norwegian number: 8 digits (first digit 2-9), optionally prefixed with +47 or 0047.");
        }
        telephone = normTelephone;
    }

    var changedFields = new List<string>();
    if (mobile is not null) changedFields.Add("mobile");
    if (telephone is not null) changedFields.Add("telephoneNumber");
    var fields = string.Join(",", changedFields);

    var caller = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    logger.LogInformation("AUDIT update-ad-phone request: caller {Caller} target {Upn} fields {Fields}", caller, normalized, fields);

    var result = await service.UpdatePhoneAsync(
        normalized,
        new SmartLockoutApi.Dtos.UpdateAdUserPhoneRequest(mobile, telephone),
        cancellationToken);

    switch (result)
    {
        case AdPhoneUpdateResult.Success:
            logger.LogInformation("AUDIT update-ad-phone success: caller {Caller} target {Upn} fields {Fields}", caller, normalized, fields);
            return Results.NoContent();
        case AdPhoneUpdateResult.NotFound nf:
            logger.LogInformation("AUDIT update-ad-phone not-found: caller {Caller} target {Upn}", caller, normalized);
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "AD user not found",
                detail: $"No Active Directory user with UPN '{nf.Upn}'.");
        case AdPhoneUpdateResult.Error err:
            logger.LogWarning("AUDIT update-ad-phone failed: caller {Caller} target {Upn} error {Error}", caller, normalized, err.Message);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Active Directory PowerShell call failed",
                detail: err.Message);
        default:
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
    }
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("UpdateAdUserPhone")
.Produces(StatusCodes.Status204NoContent)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// Read the AD department for a UPN. Read-only — no AUDIT line.
app.MapGet("/api/ad/user/{upn}/department", async (
    string upn,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on department read");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    logger.LogInformation("Reading AD department for UPN {Upn}", normalized);

    var result = await service.GetDepartmentAsync(normalized, cancellationToken);

    return result switch
    {
        AdDepartmentReadResult.Found f => Results.Ok(f.Response),
        AdDepartmentReadResult.NotFound nf => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "AD user not found",
            detail: $"No Active Directory user with UPN '{nf.Upn}'."),
        AdDepartmentReadResult.Error err => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Active Directory PowerShell call failed",
            detail: err.Message),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("GetAdUserDepartment")
.Produces<SmartLockoutApi.Dtos.AdUserDepartmentResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// State-changing endpoint, gated only by the shared API key, so every attempt
// is logged at Information with caller IP and target UPN (never the value).
// Search logs for `AUDIT update-ad-department`.
app.MapPatch("/api/ad/user/{upn}/department", async (
    string upn,
    SmartLockoutApi.Dtos.UpdateAdUserDepartmentRequest? request,
    HttpContext http,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on department update");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    // null/omitted → leave unchanged; "" → clear; non-empty → set (validated).
    var department = request?.Department;

    if (department is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Nothing to update",
            detail: "Provide 'department'. Use \"\" to clear it.");
    }

    if (department is { Length: > 0 })
    {
        if (!DepartmentValidator.TryValidate(department, out var normDepartment))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid department",
                detail: "'department' must be at most 64 characters and contain no control characters.");
        }
        department = normDepartment;
    }

    var caller = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    logger.LogInformation("AUDIT update-ad-department request: caller {Caller} target {Upn}", caller, normalized);

    var result = await service.UpdateDepartmentAsync(
        normalized,
        new SmartLockoutApi.Dtos.UpdateAdUserDepartmentRequest(department),
        cancellationToken);

    switch (result)
    {
        case AdDepartmentUpdateResult.Success:
            logger.LogInformation("AUDIT update-ad-department success: caller {Caller} target {Upn}", caller, normalized);
            return Results.NoContent();
        case AdDepartmentUpdateResult.NotFound nf:
            logger.LogInformation("AUDIT update-ad-department not-found: caller {Caller} target {Upn}", caller, normalized);
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "AD user not found",
                detail: $"No Active Directory user with UPN '{nf.Upn}'.");
        case AdDepartmentUpdateResult.Error err:
            logger.LogWarning("AUDIT update-ad-department failed: caller {Caller} target {Upn} error {Error}", caller, normalized, err.Message);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Active Directory PowerShell call failed",
                detail: err.Message);
        default:
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
    }
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("UpdateAdUserDepartment")
.Produces(StatusCodes.Status204NoContent)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// Read the AD manager for a UPN. Read-only — no AUDIT line. Returns the
// manager's UPN and display name, never the DN (it exposes OU structure).
// 500 detail is generic; the PowerShell error is logged server-side only.
app.MapGet("/api/ad/user/{upn}/manager", async (
    string upn,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on manager read");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    logger.LogInformation("Reading AD manager for UPN {Upn}", normalized);

    var result = await service.GetManagerAsync(normalized, cancellationToken);

    return result switch
    {
        AdManagerReadResult.Found f => Results.Ok(f.Response),
        AdManagerReadResult.NotFound nf => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "AD user not found",
            detail: $"No Active Directory user with UPN '{nf.Upn}'."),
        AdManagerReadResult.Error => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Active Directory PowerShell call failed",
            detail: "See server logs for details."),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("GetAdUserManager")
.Produces<SmartLockoutApi.Dtos.AdUserManagerResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// State-changing endpoint, gated only by the shared API key, so every attempt
// is logged at Information with caller IP, target UPN, new manager, and (on
// success) the previous manager so a change can be reversed from the logs.
// Search logs for `AUDIT update-ad-manager`.
app.MapPatch("/api/ad/user/{upn}/manager", async (
    string upn,
    SmartLockoutApi.Dtos.UpdateAdUserManagerRequest? request,
    HttpContext http,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on manager update");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    // null/omitted → leave unchanged; "" → clear; non-empty → manager UPN.
    var manager = request?.Manager;

    if (manager is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Nothing to update",
            detail: "Provide 'manager' as the manager's UPN. Use \"\" to clear it.");
    }

    if (manager is { Length: > 0 })
    {
        if (!UpnValidator.TryValidate(manager, out var normManager))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid manager",
                detail: "'manager' must be a UPN in the form user@domain.tld.");
        }
        manager = normManager;

        // Cheap pre-check; the service also compares resolved DNs.
        if (manager == normalized)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid manager",
                detail: "A user cannot be their own manager.");
        }
    }

    var caller = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var newManager = manager.Length == 0 ? "cleared" : manager;
    logger.LogInformation("AUDIT update-ad-manager request: caller {Caller} target {Upn} manager {Manager}", caller, normalized, newManager);

    var result = await service.UpdateManagerAsync(
        normalized,
        new SmartLockoutApi.Dtos.UpdateAdUserManagerRequest(manager),
        cancellationToken);

    IResult ManagerRejected(string reason, string detail)
    {
        logger.LogInformation("AUDIT update-ad-manager rejected: caller {Caller} target {Upn} manager {Manager} reason {Reason}", caller, normalized, newManager, reason);
        return Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Invalid manager",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["field"] = "manager" });
    }

    switch (result)
    {
        case AdManagerUpdateResult.Success s:
            logger.LogInformation("AUDIT update-ad-manager success: caller {Caller} target {Upn} manager {Manager} previous {Previous}", caller, normalized, newManager, s.PreviousManagerUpn ?? "none");
            return Results.NoContent();
        case AdManagerUpdateResult.NotFound nf:
            logger.LogInformation("AUDIT update-ad-manager not-found: caller {Caller} target {Upn}", caller, normalized);
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "AD user not found",
                detail: $"No Active Directory user with UPN '{nf.Upn}'.");
        case AdManagerUpdateResult.ManagerNotFound mnf:
            return ManagerRejected("not-found", $"No Active Directory user with UPN '{mnf.ManagerUpn}'.");
        case AdManagerUpdateResult.ManagerDisabled md:
            return ManagerRejected("disabled", $"Active Directory user '{md.ManagerUpn}' is disabled.");
        case AdManagerUpdateResult.SelfManager:
            logger.LogInformation("AUDIT update-ad-manager rejected: caller {Caller} target {Upn} manager {Manager} reason {Reason}", caller, normalized, newManager, "self");
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid manager",
                detail: "A user cannot be their own manager.");
        case AdManagerUpdateResult.Error err:
            logger.LogWarning("AUDIT update-ad-manager failed: caller {Caller} target {Upn} error {Error}", caller, normalized, err.Message);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Active Directory PowerShell call failed",
                detail: "See server logs for details.");
        default:
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
    }
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("UpdateAdUserManager")
.Produces(StatusCodes.Status204NoContent)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status422UnprocessableEntity)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// Read the AD job title for a UPN. Read-only — no AUDIT line.
// 500 detail is generic; the PowerShell error is logged server-side only.
app.MapGet("/api/ad/user/{upn}/title", async (
    string upn,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on title read");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    logger.LogInformation("Reading AD title for UPN {Upn}", normalized);

    var result = await service.GetTitleAsync(normalized, cancellationToken);

    return result switch
    {
        AdTitleReadResult.Found f => Results.Ok(f.Response),
        AdTitleReadResult.NotFound nf => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "AD user not found",
            detail: $"No Active Directory user with UPN '{nf.Upn}'."),
        AdTitleReadResult.Error => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Active Directory PowerShell call failed",
            detail: "See server logs for details."),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("GetAdUserTitle")
.Produces<SmartLockoutApi.Dtos.AdUserTitleResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// State-changing endpoint, gated only by the shared API key, so every attempt
// is logged at Information with caller IP, target UPN and the new title, and
// (on success) the previous title so a change can be reversed from the logs.
// Title is non-sensitive directory data, and TitleValidator bans control
// characters, so the value is safe to log. Search logs for `AUDIT update-ad-title`.
app.MapPatch("/api/ad/user/{upn}/title", async (
    string upn,
    SmartLockoutApi.Dtos.UpdateAdUserTitleRequest? request,
    HttpContext http,
    IAdUserDirectoryService service,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!UpnValidator.TryValidate(upn, out var normalized))
    {
        logger.LogWarning("Rejecting invalid UPN input on title update");
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid UPN",
            detail: "UPN must be in the form user@domain.tld and contain only allowed characters.");
    }

    // null/omitted → leave unchanged; "" → clear; non-empty → set (validated).
    var title = request?.Title;

    if (title is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Nothing to update",
            detail: "Provide 'title'. Use \"\" to clear it.");
    }

    if (title is { Length: > 0 })
    {
        if (!TitleValidator.TryValidate(title, out var normTitle))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid title",
                detail: "'title' must be at most 128 characters and contain no control characters.");
        }
        title = normTitle;
    }

    var caller = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var newTitle = title.Length == 0 ? "cleared" : title;
    logger.LogInformation("AUDIT update-ad-title request: caller {Caller} target {Upn} title {Title}", caller, normalized, newTitle);

    var result = await service.UpdateTitleAsync(
        normalized,
        new SmartLockoutApi.Dtos.UpdateAdUserTitleRequest(title),
        cancellationToken);

    switch (result)
    {
        case AdTitleUpdateResult.Success s:
            logger.LogInformation("AUDIT update-ad-title success: caller {Caller} target {Upn} title {Title} previous {Previous}", caller, normalized, newTitle, s.PreviousTitle ?? "none");
            return Results.NoContent();
        case AdTitleUpdateResult.NotFound nf:
            logger.LogInformation("AUDIT update-ad-title not-found: caller {Caller} target {Upn}", caller, normalized);
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "AD user not found",
                detail: $"No Active Directory user with UPN '{nf.Upn}'.");
        case AdTitleUpdateResult.Error err:
            logger.LogWarning("AUDIT update-ad-title failed: caller {Caller} target {Upn} error {Error}", caller, normalized, err.Message);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Active Directory PowerShell call failed",
                detail: "See server logs for details.");
        default:
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
    }
})
.AddEndpointFilter<ApiKeyEndpointFilter>()
.WithName("UpdateAdUserTitle")
.Produces(StatusCodes.Status204NoContent)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

app.Run();
