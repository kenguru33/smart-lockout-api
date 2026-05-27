using System.Text.RegularExpressions;

namespace SmartLockoutApi.Validation;

// Validation for the phone attributes, mirroring UpnValidator. AD stores these
// as free text, so formatting is allowed (digits plus the usual phone
// punctuation), but the value must actually be a phone number: it must carry a
// plausible count of digits. A leading '+' (E.164) is permitted but optional.
// Used only to vet a value that is being *set*; an empty string (clear) and
// null (leave unchanged) are handled by the endpoint, not here.
public static partial class PhoneNumberValidator
{
    private const int MaxPhoneLength = 32;
    // E.164 allows up to 15 digits; require at least 3 so short internal
    // extensions are still accepted but punctuation-only junk is rejected.
    private const int MinDigits = 3;
    private const int MaxDigits = 15;

    // Optional single leading '+', then digits interleaved with the usual
    // separators. Anchored, so the whole string must match.
    [GeneratedRegex(@"^\+?[0-9 ().\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ShapeRegex();

    // Validates a non-empty value destined for AD. Trims surrounding
    // whitespace and returns the normalized form to store.
    public static bool TryValidate(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxPhoneLength) return false;
        if (!ShapeRegex().IsMatch(trimmed)) return false;

        var digitCount = trimmed.Count(char.IsDigit);
        if (digitCount < MinDigits || digitCount > MaxDigits) return false;

        normalized = trimmed;
        return true;
    }
}
