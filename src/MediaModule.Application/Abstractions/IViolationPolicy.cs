using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IViolationPolicy
{
    ViolationDecision RegisterViolation(string filePath);

    void Reset(string filePath);
}
