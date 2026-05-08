namespace MediaModule.Domain.Entities;

public sealed record FileDetectedEvent(string FullPath, WatcherChangeTypes ChangeType, DateTime OccurredAtUtc);
