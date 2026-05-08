using MediaModule.Domain.Entities;

namespace MediaModule.Application.Abstractions;

public interface IFileEventSource : IDisposable
{
    event Func<FileDetectedEvent, CancellationToken, Task>? FileDetected;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
