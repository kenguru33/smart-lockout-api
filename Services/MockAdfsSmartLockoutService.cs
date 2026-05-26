using SmartLockoutApi.Dtos;

namespace SmartLockoutApi.Services;

// Dev-only stand-in for PowerShellAdfsSmartLockoutService. Registered when
// the host is in the Development environment so the API can be exercised
// end-to-end on machines without the ADFS PowerShell module.
//
// Reserved UPN local-parts steer the response shape:
//   notfound@*  → 404
//   error@*     → 500
//   locked@*    → 200 with isLockedOut=true (deterministic)
//   anything else → 200 with a randomized record (varies per request).
internal sealed class MockAdfsSmartLockoutService : IAdfsSmartLockoutService
{
    private static readonly string[] IpPool =
    {
        "203.0.113.10", "203.0.113.11", "203.0.113.42",
        "198.51.100.7", "198.51.100.99", "192.0.2.55"
    };

    public Task<SmartLockoutResult> GetAsync(string upn, CancellationToken cancellationToken)
    {
        var local = upn[..upn.IndexOf('@')].ToLowerInvariant();

        SmartLockoutResult result = local switch
        {
            "notfound" => new SmartLockoutResult.NotFound(upn),
            "error" => new SmartLockoutResult.Error("Mock error: simulated AD FS failure."),
            "locked" => new SmartLockoutResult.Found(new SmartLockoutResponse(
                UserPrincipalName: upn,
                IsLockedOut: true,
                FamiliarLockout: false,
                UnknownLockout: true,
                BadPwdCountFamiliar: 0,
                BadPwdCountUnknown: 7,
                LastFailedAuthFamiliar: null,
                LastFailedAuthUnknown: DateTimeOffset.UtcNow.AddMinutes(-2),
                FamiliarIps: new[] { "203.0.113.10", "203.0.113.11" })),
            _ => new SmartLockoutResult.Found(BuildRandom(upn)),
        };

        return Task.FromResult(result);
    }

    private static SmartLockoutResponse BuildRandom(string upn)
    {
        var rng = Random.Shared;
        var familiarLocked = rng.Next(10) < 2;
        var unknownLocked = rng.Next(10) < 3;
        var isLocked = familiarLocked || unknownLocked;

        var badFamiliar = familiarLocked ? rng.Next(5, 11) : rng.Next(0, 4);
        var badUnknown = unknownLocked ? rng.Next(5, 11) : rng.Next(0, 4);

        DateTimeOffset? lastFamiliar = badFamiliar > 0
            ? DateTimeOffset.UtcNow.AddMinutes(-rng.Next(1, 180))
            : null;
        DateTimeOffset? lastUnknown = badUnknown > 0
            ? DateTimeOffset.UtcNow.AddMinutes(-rng.Next(1, 180))
            : null;

        var ipCount = rng.Next(1, 4);
        var ips = new HashSet<string>();
        while (ips.Count < ipCount)
        {
            ips.Add(IpPool[rng.Next(IpPool.Length)]);
        }

        return new SmartLockoutResponse(
            UserPrincipalName: upn,
            IsLockedOut: isLocked,
            FamiliarLockout: familiarLocked,
            UnknownLockout: unknownLocked,
            BadPwdCountFamiliar: badFamiliar,
            BadPwdCountUnknown: badUnknown,
            LastFailedAuthFamiliar: lastFamiliar,
            LastFailedAuthUnknown: lastUnknown,
            FamiliarIps: ips.ToArray());
    }
}
