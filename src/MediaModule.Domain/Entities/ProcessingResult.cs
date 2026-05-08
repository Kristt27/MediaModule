namespace MediaModule.Domain.Entities;

public enum ProcessingResult
{
    Success = 0,
    Blocked = 1,
    SavedWithIgnoredViolation = 2,
    DuplicateFound = 3,
    DuplicateRenamed = 3,
    Failed = 4,
    Skipped = 5,
    CorrectedByUser = 6,
}
