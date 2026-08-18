namespace AssetProvenanceHelper.Models;

public sealed class ValidationResult
{
    public bool IsValid { get; }

    public IReadOnlyList<string> Errors { get; }

    private ValidationResult(
        bool isValid,
        IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    public static ValidationResult Success()
    {
        return new ValidationResult(
            true,
            Array.Empty<string>());
    }

    public static ValidationResult Failure(
        params string[] errors)
    {
        return new ValidationResult(
            false,
            errors
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray());
    }

    public static ValidationResult Failure(
        IEnumerable<string> errors)
    {
        return new ValidationResult(
            false,
            errors
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray());
    }
}
