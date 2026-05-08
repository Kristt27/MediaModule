using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IDuplicateResolutionService
{
    Task<DuplicateResolutionAction> ResolveAsync(
        string currentFilePath,
        string duplicateFilePath,
        OrderData? orderData,
        CancellationToken cancellationToken);
}
