// SmartLockoutApi — internal AD FS Extranet Smart Lockout query API.
//
// RUNTIME: the process must run on a Windows host where the "ADFS" PowerShell
// module is available (an AD FS server, or an admin host with AD FS RSAT).
//
// Auth: X-API-Key header, validated by ApiKeyEndpointFilter against
// ApiKey:Keys from configuration. TLS is the deployer's responsibility.

using Microsoft.OpenApi.Models;
using SmartLockoutApi.Services;
using SmartLockoutApi.Validation;

var builder = WebApplication.CreateBuilder(args);

// The PowerShell singleton opens a long-lived Runspace at construction and
// imports the ADFS module via -UseWindowsPowerShell into it; each request
// gets a fresh PowerShell instance attached to that runspace inside GetAsync.
builder.Services.AddSingleton<IAdfsSmartLockoutService, PowerShellAdfsSmartLockoutService>();
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

// Force the PowerShell service to construct now so an ADFS-module import
// failure stops startup instead of becoming a 500 on the first request.
app.Services.GetRequiredService<IAdfsSmartLockoutService>();

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

app.Run();
