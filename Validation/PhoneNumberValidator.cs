using System.Text.RegularExpressions;

namespace SmartLockoutApi.Validation;

// Validation for the phone attributes, mirroring UpnValidator. Only Norwegian
// numbers are accepted: 8 national digits (first digit 2-9, per the Norwegian
// numbering plan — 0/1 are reserved for short/special codes), optionally
// prefixed with +47 or 0047. The usual separators (space, hyphen, parens, dot)
// are allowed for formatting and stripped before checking. Accepted values are
// normalized to canonical E.164 (+47XXXXXXXX) so AD data stays consistent.
//
// Used only to vet a value being *set*; an empty string (clear) and null
// (leave unchanged) are handled by the endpoint, not here.
public static partial class PhoneNumberValidator
{
    private const int MaxPhoneLength = 32;

    // Formatting separators to strip before validating.
    [GeneratedRegex(@"[ ().\-]", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();

    // After separators are removed: optional +47/0047, then 8 digits starting 2-9.
    // Group 1 captures the 8 national digits.
    [GeneratedRegex(@"^(?:\+47|0047)?([2-9]\d{7})$", RegexOptions.CultureInvariant)]
    private static partial Regex NorwegianRegex();

    // Validates a non-empty value destined for AD and returns the normalized
    // E.164 form (+47XXXXXXXX) to store.
    public static bool TryValidate(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxPhoneLength) return false;

        var compact = SeparatorRegex().Replace(trimmed, string.Empty);
        var match = NorwegianRegex().Match(compact);
        if (!match.Success) return false;

        normalized = "+47" + match.Groups[1].Value;
        return true;
    }
}
