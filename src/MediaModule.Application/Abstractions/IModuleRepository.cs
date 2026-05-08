using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IModuleRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<string?> FindByHashAsync(string hash, string currentFilePath, CancellationToken cancellationToken);

    Task<bool> HasFileHashAsync(string filePath, string hash, CancellationToken cancellationToken);

    Task UpsertFileHashAsync(string filePath, string hash, OrderData? orderData, CancellationToken cancellationToken);

    Task SaveTagsAsync(string filePath, IReadOnlyCollection<TagItem> tags, OrderData? orderData, CancellationToken cancellationToken);

    Task SaveLogAsync(ProcessingLogEntry entry, OrderData? orderData, CancellationToken cancellationToken);
}
