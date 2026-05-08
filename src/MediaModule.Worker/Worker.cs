using MediaModule.Application.Abstractions;
using MediaModule.Application.Services;

namespace MediaModule.Worker;

public sealed class Worker : BackgroundService
{
    private readonly IFileEventSource _fileEventSource;
    private readonly IModuleRepository _moduleRepository;
    private readonly IFileNotificationService _notificationService;
    private readonly FileProcessingOrchestrator _orchestrator;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IFileEventSource fileEventSource,
        IModuleRepository moduleRepository,
        IFileNotificationService notificationService,
        FileProcessingOrchestrator orchestrator,
        ILogger<Worker> logger)
    {
        _fileEventSource = fileEventSource;
        _moduleRepository = moduleRepository;
        _notificationService = notificationService;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _moduleRepository.InitializeAsync(stoppingToken);

        _fileEventSource.FileDetected += _orchestrator.ProcessAsync;
        await _fileEventSource.StartAsync(stoppingToken);

        _logger.LogInformation("MediaModule worker started.");
        await _notificationService.NotifyAsync(
            "MediaModule: модуль запущен",
            "Фоновый модуль активен. Новые изображения будут проверяться перед записью в журнал.",
            stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("MediaModule worker stopping.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _fileEventSource.FileDetected -= _orchestrator.ProcessAsync;
        await _fileEventSource.StopAsync(cancellationToken);
        _fileEventSource.Dispose();

        await base.StopAsync(cancellationToken);
    }
}
