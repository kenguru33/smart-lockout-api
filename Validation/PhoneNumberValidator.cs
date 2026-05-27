using System.Text.RegularExpressions;

namespace SmartLockoutApi.Validation;

// Defence-in-depth for the phone attributes, mirroring UpnValidator. AD stores
// these as free text, so the policy is intentionally permissive: digits plus
// the usual phone punctuation, capped in length. Used only to vet a value that
// is being *set*; an empty string (clear) and null (leave unchanged) are
// handled by the endpoint, not here.
public static partial class PhoneNumberValidator
{
    private const int MaxPhoneLength = 32;

    [GeneratedRegex(@"^[0-9 ()+.\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    // Validates a non-empty value destined for AD. Trims surrounding
    // whitespace and returns the normalized form to store.
    public static bool TryValidate(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxPhoneLength) return false;
        if (!PhoneRegex().IsMatch(trimmed)) return false;

        normalized = trimmed;
        return true;
    }
}
