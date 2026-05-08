namespace MediaModule.Domain.Entities;

public sealed class FileValidationResult
{
    public bool IsNameValid { get; init; }

    public bool IsPathValid { get; init; }

    public string? RecommendedDirectory { get; init; }

    public string? RecommendedFileName { get; init; }

    public List<string> FailureReasons { get; } = new();

    public bool HasViolations => !IsNameValid || !IsPathValid;

    public static FileValidationResult Success(string? recommendedDirectory = null, string? recommendedFileName = null) =>
        new()
        {
            IsNameValid = true,
            IsPathValid = true,
            RecommendedDirectory = recommendedDirectory,
            RecommendedFileName = recommendedFileName,
        };

    public static FileValidationResult Failed(
        bool isNameValid,
        bool isPathValid,
        string? recommendedDirectory = null,
        string? recommendedFileName = null,
        params string[] reasons)
    {
        var result = new FileValidationResult
        {
            IsNameValid = isNameValid,
            IsPathValid = isPathValid,
            RecommendedDirectory = recommendedDirectory,
            RecommendedFileName = recommendedFileName,
        };

        result.FailureReasons.AddRange(reasons.Where(x => !string.IsNullOrWhiteSpace(x)));
        return result;
    }
}
