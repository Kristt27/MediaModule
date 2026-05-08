using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Domain.Entities;
using MediaModule.Infrastructure.PathResolution;
using Microsoft.Extensions.Options;

namespace MediaModule.Infrastructure.Integration;

public sealed class MockElmaClient : IElmaClient
{
    private readonly ModuleOptions _options;

    public MockElmaClient(IOptions<ModuleOptions> options)
    {
        _options = options.Value;
    }

    public Task<OrderData?> TryResolveOrderAsync(string filePath, CancellationToken cancellationToken)
    {
        var orders = GetConfiguredOrders();
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var chunks = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (chunks.Length >= 2)
        {
            var byName = orders.FirstOrDefault(
                x => string.Equals(x.ClientName, chunks[0], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.ProductType, chunks[1], StringComparison.OrdinalIgnoreCase));

            if (byName is not null)
            {
                return Task.FromResult<OrderData?>(byName);
            }
        }

        var root = ModulePathResolver.Resolve(_options.RootDirectory);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var relative = Path.GetRelativePath(root, directory);
            var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var segments = relative.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length >= 2)
            {
                var byPath = orders.FirstOrDefault(
                    x => string.Equals(x.ClientName, segments[0], StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.ProductType, segments[1], StringComparison.OrdinalIgnoreCase));

                if (byPath is not null)
                {
                    return Task.FromResult<OrderData?>(byPath);
                }
            }
        }

        return Task.FromResult<OrderData?>(null);
    }

    public Task<IReadOnlyCollection<OrderData>> GetOrdersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<OrderData>>(GetConfiguredOrders());

    private IReadOnlyCollection<OrderData> GetConfiguredOrders()
    {
        return _options.MiniCrm.Orders.Count > 0
            ? _options.MiniCrm.Orders
            : _options.ElmaMock.Orders;
    }
}
