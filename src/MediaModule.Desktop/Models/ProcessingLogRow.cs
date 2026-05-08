namespace MediaModule.Desktop.Models;

public sealed class ProcessingLogRow
{
    public int Id { get; init; }

    public int FileId { get; init; }

    public string OperationTimeUtc { get; init; } = string.Empty;

    public string OperationTimeDisplay
    {
        get
        {
            return DateTimeOffset.TryParse(
                OperationTimeUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : OperationTimeUtc;
        }
    }

    public string FileName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public bool ErrorIgnored { get; init; }

    public string Message { get; init; } = string.Empty;

    public string OrderId { get; init; } = string.Empty;

    public string DuplicateOf { get; init; } = string.Empty;

    public string TagsJson { get; init; } = string.Empty;

    public IReadOnlyCollection<TagRow> NormalizedTags { get; set; } = Array.Empty<TagRow>();

    public int TagsCount => NormalizedTags.Count;
}
