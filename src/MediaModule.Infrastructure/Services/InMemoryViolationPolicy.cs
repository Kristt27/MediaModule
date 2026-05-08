using System.Collections.Concurrent;
using MediaModule.Application.Abstractions;
using MediaModule.Domain.Entities;

namespace MediaModule.Infrastructure.Services;

public sealed class InMemoryViolationPolicy : IViolationPolicy
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public ViolationDecision RegisterViolation(string filePath)
    {
        var attempt = _attempts.AddOrUpdate(filePath, 1, (_, current) => current + 1);
        return attempt == 1
            ? new ViolationDecision(ShouldBlock: true, IsIgnored: false, AttemptNumber: attempt)
            : new ViolationDecision(ShouldBlock: false, IsIgnored: true, AttemptNumber: attempt);
    }

    public void Reset(string filePath)
    {
        _attempts.TryRemove(filePath, out _);
    }
}
