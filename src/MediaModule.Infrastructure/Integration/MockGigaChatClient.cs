using MediaModule.Application.Abstractions;
using MediaModule.Domain.Entities;

namespace MediaModule.Infrastructure.Integration;

public sealed class MockGigaChatClient : IGigaChatClient
{
    public Task<IReadOnlyCollection<TagItem>> GenerateTagsAsync(string filePath, OrderData? orderData, CancellationToken cancellationToken)
    {
        var tags = new List<TagItem>();

        var extension = Path.GetExtension(filePath).Trim('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(extension))
        {
            tags.Add(new TagItem("extension", extension));
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var chunks = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (chunks.Length >= 2)
        {
            tags.Add(new TagItem("object_type", chunks[1].ToLowerInvariant()));
        }

        var fileNameLower = fileName.ToLowerInvariant();
        if (fileNameLower.Contains("minimal"))
        {
            tags.Add(new TagItem("style", "minimalism"));
        }
        else if (fileNameLower.Contains("retro"))
        {
            tags.Add(new TagItem("style", "retro"));
        }
        else
        {
            tags.Add(new TagItem("style", "generic"));
        }

        if (orderData is not null)
        {
            tags.Add(new TagItem("order_id", orderData.OrderId));
            tags.Add(new TagItem("client", orderData.ClientName));
            tags.Add(new TagItem("product", orderData.ProductType));
        }

        return Task.FromResult<IReadOnlyCollection<TagItem>>(tags);
    }
}
