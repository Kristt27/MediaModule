using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IOrderSelectionService
{
    Task<OrderData?> SelectOrderAsync(
        string filePath,
        IReadOnlyCollection<OrderData> orders,
        CancellationToken cancellationToken);
}
