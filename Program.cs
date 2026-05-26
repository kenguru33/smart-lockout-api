// SmartLockoutApi — internal AD FS Extranet Smart Lockout query API.
//
// RUNTIME REQUIREMENT: this process must run on a Windows host where the
// "ADFS" PowerShell module is available (an AD FS server itself, or an
// admin host with AD FS RSAT). On any other host the PowerShell invocation
// will fail with "module not found" and every request returns 500.
//
// DO NOT EXPOSE PUBLICLY. There is no authentication wired up yet —
// see the TODO in MapGet below.

using SmartLockoutApi.Services;
using SmartLockoutApi.Validation;

var builder = WebApplication.CreateBuilder(args);

// Singleton: the cached InitialSessionState (with ADFS module import) is
// reused across requests; each request gets a fresh PowerShell instance
// inside GetAsync.
builder.Services.AddSingleton<IAdfsSmartLockoutService, PowerShellAdfsSmartLockoutService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// TODO(auth): require authentication before deploying anywhere reachable.
// Preferred options: Windows Authentication (Negotiate/Kerberos) for an
// on-prem AD FS host, or Microsoft.Identity.Web for Entra ID. Until that
// is in place, bind the listener to a non-routable interface only.
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
.WithName("GetAdfsSmartLockout")
.Produces<SmartLockoutApi.Dtos.SmartLockoutResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status404NotFound)
.ProducesProblem(StatusCodes.Status500InternalServerError);

app.Run();
