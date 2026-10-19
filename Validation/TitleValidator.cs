namespace SmartLockoutApi.Validation;

// Validates a non-empty job title destined for AD and returns the trimmed form
// to store. Title is free text, so the only constraints are the length cap and
// a ban on control characters (which also keeps the value safe to write into
// the AUDIT log line). Empty-string "clear" semantics are handled by the
// endpoint, not here.
public static class TitleValidator
{
    // The AD 'title' attribute (rangeUpper) caps at 128 characters, as does
    // Entra ID's jobTitle, so accepted values survive directory sync unchanged.
    private const int MaxTitleLength = 128;

    public static bool TryValidate(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.Length > MaxTitleLength) return false;
        if (trimmed.Any(char.IsControl)) return false;

        normalized = trimmed;
        return true;
    }
}
