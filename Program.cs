// SmartLockoutApi — internal AD FS Extranet Smart Lockout query API.
//
// RUNTIME: the process must run on a Windows host where the "ADFS" PowerShell
// module is available (an AD FS server, or an admin host with AD FS RSAT).
//
// Auth: X-API-Key header, validated by ApiKeyEndpointFilter against
// ApiKey:Keys from configuration. TLS is the deployer's responsibility.

using System.Security.Authentication;
using Microsoft.OpenApi.Models;
using SmartLockoutApi.Services;
using SmartLockoutApi.Tls;
using SmartLockoutApi.Validation;

var builder = WebApplication.CreateBuilder(args);

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
// AD mobile/telephone read+update endpoints. Same singleton-with-cached-runspace
// rationale as the AD FS service.
builder.Services.AddSingleton<IAdUserPhoneService, PowerShellAdUserPhoneService>();
builder.Services.AddSingleton<ApiKeyEndpointFilter>();

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

var app = builder.Build();

// Force the PowerShell services to construct now so a module-import failure
// stops startup instead of becoming a 500 on the first request.
app.Services.GetRequiredService<IAdfsSmartLockoutService>();
app.Services.GetRequiredService<IAdUserPhoneService>();

// Same idea for the TLS cert provider: resolve it now so a missing / expired /
// no-private-key cert stops startup instead of failing the first TLS handshake.
if (useWindowsCertStoreTls)
{
    app.Services.GetRequiredService<IServerCertificateProvider>();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var configuredKeys = builder.Configuration.GetSection("ApiKey:Keys").Get<string[]>() ?? Array.Empty<string>();
if (!configuredKeys.Any(k => !string.IsNullOrWhiteSpace(k)))
{
    app.Logger.LogWarning(
        "ApiKey:Keys is empty; every request will be rejected with 401. " +
        "Set ApiKey__Keys__0 (and __1 during rotation) before deploying.");
}

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
    IAdUserPhoneService service,
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
    IAdUserPhoneService service,
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

app.Run();
