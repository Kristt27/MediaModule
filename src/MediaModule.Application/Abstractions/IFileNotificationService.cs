namespace MediaModule.Application.Abstractions;

public interface IFileNotificationService
{
    Task NotifyAsync(string title, string message, CancellationToken cancellationToken);
}
