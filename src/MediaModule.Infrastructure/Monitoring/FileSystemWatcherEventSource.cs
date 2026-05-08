using System.Collections.Concurrent;
using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Domain.Entities;
using MediaModule.Infrastructure.PathResolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaModule.Infrastructure.Monitoring;

public sealed class FileSystemWatcherEventSource : IFileEventSource
{
    private readonly ModuleOptions _options;
    private readonly ILogger<FileSystemWatcherEventSource> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, PendingFileEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignoredDirectories = new();
    private readonly HashSet<string> _allowedExtensions;

    private bool _started;

    public FileSystemWatcherEventSource(IOptions<ModuleOptions> options, ILogger<FileSystemWatcherEventSource> logger)
    {
        _options = options.Value;
        _logger = logger;
        _ignoredDirectories = _options.IgnoredDirectories
            .Select(ModulePathResolver.Resolve)
            .Concat(GetRejectedDirectories())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _allowedExtensions = _options.AllowedExtensions
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .Select(static extension => extension.StartsWith('.') ? extension : $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public event Func<FileDetectedEvent, CancellationToken, Task>? FileDetected;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        var directories = GetDirectoriesToWatch();

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogInformation("Каталог мониторинга создан: {Directory}", directory);
            }

            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = _options.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, args) => HandleEvent(args.FullPath, WatcherChangeTypes.Created, cancellationToken);
            watcher.Changed += (_, args) => HandleEvent(args.FullPath, WatcherChangeTypes.Changed, cancellationToken);
            watcher.Renamed += (_, args) => HandleEvent(args.FullPath, WatcherChangeTypes.Renamed, cancellationToken);
            watcher.Error += (_, args) =>
            {
                var exception = args.GetException();
                _logger.LogError(exception, "Ошибка FileSystemWatcher для каталога {Directory}", directory);
            };

            _watchers.Add(watcher);
            _logger.LogInformation("Мониторинг включен: {Directory}", directory);
        }

        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        CancelPendingEvents();
        _started = false;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        CancelPendingEvents();
    }

    private void HandleEvent(string fullPath, WatcherChangeTypes changeType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        if (_ignoredDirectories.Any(x => fullPath.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (Directory.Exists(fullPath) || !IsAllowedGraphicFile(fullPath))
        {
            return;
        }

        var pendingEvent = new PendingFileEvent(changeType, DateTime.UtcNow, new CancellationTokenSource());

        if (_pendingEvents.TryGetValue(fullPath, out var previous))
        {
            previous.Cancellation.Cancel();
            previous.Cancellation.Dispose();
        }

        _pendingEvents[fullPath] = pendingEvent;
        _ = PublishWhenSettledAsync(fullPath, pendingEvent, cancellationToken);
    }

    private List<string> GetDirectoriesToWatch()
    {
        var configuredDirectories = _options.MonitoredDirectories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(ModulePathResolver.Resolve)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configuredDirectories.Count > 0)
        {
            return configuredDirectories;
        }

        return
        [
            ModulePathResolver.Resolve(_options.RootDirectory),
        ];
    }

    private IEnumerable<string> GetRejectedDirectories()
    {
        var configured = _options.RejectedFilesDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "rejected";
        }

        yield return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    private async Task PublishWhenSettledAsync(string fullPath, PendingFileEvent pendingEvent, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.EventDebounceMilliseconds, pendingEvent.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_pendingEvents.TryGetValue(fullPath, out var current) || !ReferenceEquals(current, pendingEvent))
        {
            pendingEvent.Cancellation.Dispose();
            return;
        }

        _pendingEvents.TryRemove(fullPath, out _);

        var detectedEvent = new FileDetectedEvent(fullPath, pendingEvent.ChangeType, pendingEvent.OccurredAtUtc);
        _logger.LogInformation("Событие файла поставлено в обработку: {ChangeType} {Path}", pendingEvent.ChangeType, fullPath);
        pendingEvent.Cancellation.Dispose();
        await PublishAsync(detectedEvent, cancellationToken);
    }

    private void CancelPendingEvents()
    {
        foreach (var pending in _pendingEvents.Values)
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }

        _pendingEvents.Clear();
    }

    private bool IsAllowedGraphicFile(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        return !string.IsNullOrWhiteSpace(extension) && _allowedExtensions.Contains(extension);
    }

    private async Task PublishAsync(FileDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        var handlers = FileDetected;
        if (handlers is null)
        {
            return;
        }

        foreach (var singleHandler in handlers.GetInvocationList().Cast<Func<FileDetectedEvent, CancellationToken, Task>>())
        {
            try
            {
                await singleHandler(detectedEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработчика события для файла {Path}", detectedEvent.FullPath);
            }
        }
    }

    private sealed record PendingFileEvent(
        WatcherChangeTypes ChangeType,
        DateTime OccurredAtUtc,
        CancellationTokenSource Cancellation);
}
