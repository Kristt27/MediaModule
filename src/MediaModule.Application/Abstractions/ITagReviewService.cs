using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface ITagReviewService
{
    Task<bool> RequestTagApprovalAsync(
        string filePath,
        IReadOnlyCollection<TagItem> tags,
        OrderData? orderData,
        CancellationToken cancellationToken);
}
