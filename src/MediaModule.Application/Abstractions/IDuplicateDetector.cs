namespace MediaModule.Application.Abstractions;

public interface IDuplicateDetector
{
    Task<string> ComputePerceptualHashAsync(string filePath, CancellationToken cancellationToken);
}
