using System.Security.Cryptography;
using System.Text;

namespace SmartLockoutApi.Validation;

internal sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-API-Key";
    private const string ChallengeScheme = "ApiKey";

    private readonly byte[][] _acceptedKeys;
    private readonly ILogger<ApiKeyEndpointFilter> _logger;

    public ApiKeyEndpointFilter(IConfiguration configuration, ILogger<ApiKeyEndpointFilter> logger)
    {
        _logger = logger;
        var raw = configuration.GetSection("ApiKey:Keys").Get<string[]>() ?? Array.Empty<string>();
        _acceptedKeys = raw
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Encoding.UTF8.GetBytes(k.Trim()))
            .ToArray();
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (_acceptedKeys.Length == 0)
        {
            _logger.LogWarning("Rejecting request: no API keys configured");
            return Unauthorized(http);
        }

        var headerValues = http.Request.Headers[HeaderName];
        if (headerValues.Count != 1)
        {
            _logger.LogWarning("Rejecting request: API key header missing or multi-valued");
            return Unauthorized(http);
        }

        var supplied = headerValues[0];
        if (string.IsNullOrEmpty(supplied))
        {
            _logger.LogWarning("Rejecting request: empty API key header");
            return Unauthorized(http);
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var match = false;
        // Iterate every entry even after a match so timing doesn't reveal which key (or how many) matched.
        foreach (var accepted in _acceptedKeys)
        {
            if (accepted.Length == suppliedBytes.Length
                && CryptographicOperations.FixedTimeEquals(accepted, suppliedBytes))
            {
                match = true;
            }
        }

        if (!match)
        {
            _logger.LogWarning("Rejecting request: invalid API key");
            return Unauthorized(http);
        }

        return await next(context);
    }

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = ChallengeScheme;
        return Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: $"A valid {HeaderName} header is required.");
    }
}
