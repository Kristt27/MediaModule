namespace MediaModule.Domain.Entities;

public sealed class ProcessingLogEntry
{
    public required string FileName { get; init; }

    public required string FilePath { get; init; }

    public required string UserName { get; init; }

    public required DateTime OperationTimeUtc { get; init; }

    public required ProcessingResult Result { get; init; }

    public bool ErrorIgnored { get; init; }

    public string? Message { get; init; }

    public string? DuplicateOf { get; init; }

    public string? OrderId { get; init; }

    public IReadOnlyCollection<TagItem> Tags { get; init; } = Array.Empty<TagItem>();
}
