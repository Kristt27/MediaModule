using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IElmaClient
{
    Task<OrderData?> TryResolveOrderAsync(string filePath, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<OrderData>> GetOrdersAsync(CancellationToken cancellationToken);
}
