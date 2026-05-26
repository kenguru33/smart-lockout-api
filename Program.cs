// SmartLockoutApi — internal AD FS Extranet Smart Lockout query API.
//
// RUNTIME: in non-Development environments the process must run on a Windows
// host where the "ADFS" PowerShell module is available (an AD FS server, or
// an admin host with AD FS RSAT). In Development a mock service stands in so
// the API can be exercised end-to-end without AD FS.
//
// Auth: X-API-Key header, validated by ApiKeyEndpointFilter against
// ApiKey:Keys from configuration. TLS is the deployer's responsibility.

using Microsoft.OpenApi.Models;
using SmartLockoutApi.Services;
using SmartLockoutApi.Validation;

var builder = WebApplication.CreateBuilder(args);

// In Development the ADFS PS module is usually absent, so substitute a mock.
// The PowerShell singleton caches an InitialSessionState (with the ADFS
// module imported) across requests; each request gets a fresh PowerShell
// instance inside GetAsync.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IAdfsSmartLockoutService, MockAdfsSmartLockoutService>();
}
else
{
    builder.Services.AddSingleton<IAdfsSmartLockoutService, PowerShellAdfsSmartLockoutService>();
}
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

app.Run();
