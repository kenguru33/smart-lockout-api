using System.Text.RegularExpressions;

namespace SmartLockoutApi.Validation;

public static partial class UpnValidator
{
    private const int MaxUpnLength = 256;

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex UpnRegex();

    public static bool TryValidate(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxUpnLength) return false;
        if (!UpnRegex().IsMatch(trimmed)) return false;

        normalized = trimmed.ToLowerInvariant();
        return true;
    }
}
