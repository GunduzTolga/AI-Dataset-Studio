namespace AIDatasetStudio.Domain.Common;

internal static class Guard
{
    public static string NotBlank(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{paramName} must not be empty.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{paramName} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static Guid NotEmpty(Guid value, string paramName)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException($"{paramName} must not be an empty identifier.");
        }

        return value;
    }
}
