using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IFileCorrectionService
{
    Task<FileCorrectionAction> RequestCorrectionAsync(
        string rejectedFilePath,
        string recommendedDirectory,
        string recommendedFileName,
        string reason,
        CancellationToken cancellationToken);
}
