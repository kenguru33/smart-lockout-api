namespace SmartLockoutApi.Validation;

// Validates a non-empty department value destined for AD and returns the
// trimmed form to store. Department is free text, so the only constraints are
// the AD schema length cap and a ban on control characters. Empty-string
// "clear" semantics are handled by the endpoint, not here.
public static class DepartmentValidator
{
    // The AD 'department' attribute (rangeUpper) caps at 64 characters.
    private const int MaxDepartmentLength = 64;

    public static bool TryValidate(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxDepartmentLength) return false;
        if (trimmed.Any(char.IsControl)) return false;

        normalized = trimmed;
        return true;
    }
}
