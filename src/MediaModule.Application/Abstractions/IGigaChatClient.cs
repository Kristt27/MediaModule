using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IGigaChatClient
{
    Task<IReadOnlyCollection<TagItem>> GenerateTagsAsync(string filePath, OrderData? orderData, CancellationToken cancellationToken);
}
