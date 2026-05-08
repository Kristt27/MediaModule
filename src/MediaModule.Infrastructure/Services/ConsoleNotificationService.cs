using MediaModule.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace MediaModule.Infrastructure.Services;

public sealed class ConsoleNotificationService : IFileNotificationService
{
    private readonly ILogger<ConsoleNotificationService> _logger;

    public ConsoleNotificationService(ILogger<ConsoleNotificationService> logger)
    {
        _logger = logger;
    }

    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        _logger.LogWarning("{Title}: {Message}", title, message);
        return Task.CompletedTask;
    }
}
