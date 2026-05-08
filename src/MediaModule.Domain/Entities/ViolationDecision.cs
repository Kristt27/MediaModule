namespace MediaModule.Domain.Entities;

public sealed record ViolationDecision(bool ShouldBlock, bool IsIgnored, int AttemptNumber);
