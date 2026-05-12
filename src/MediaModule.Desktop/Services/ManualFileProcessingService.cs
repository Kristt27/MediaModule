using System.IO;
using System.Collections.Concurrent;
using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Application.Services;
using MediaModule.Domain.Entities;
using MediaModule.Infrastructure.Integration;
using MediaModule.Infrastructure.Persistence;
using MediaModule.Infrastructure.Services;
using MediaModule.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaModule.Desktop.Services;

public sealed class ManualFileProcessingService
{
    private readonly ModuleOptions _options;
    private readonly SqliteModuleRepository _repository;
    private readonly ManualOrderContextElmaClient _elmaClient;
    private readonly IFileRuleValidator _ruleValidator;
    private readonly IOrderSelectionService _orderSelectionService;
    private readonly IFileCorrectionService _fileCorrectionService;
    private readonly FileProcessingOrchestrator _orchestrator;
    private readonly IProgress<ManualProcessingNotification> _notifications;

    public ManualFileProcessingService(
        WorkerSettingsService settingsService,
        IProgress<ManualProcessingNotification> notifications)
    {
        _notifications = notifications;
        _options = settingsService.LoadModuleOptions();
        var monitor = new StaticOptionsMonitor<ModuleOptions>(_options);
        var options = Options.Create(_options);
        _repository = new SqliteModuleRepository(options);
        _elmaClient = new ManualOrderContextElmaClient(new RealElmaClient(options, NullLogger<RealElmaClient>.Instance));
        _ruleValidator = new RegexFileRuleValidator(monitor);
        _orderSelectionService = new WindowsOrderSelectionService();
        _fileCorrectionService = new WindowsFileCorrectionService();

        _orchestrator = new FileProcessingOrchestrator(
            _ruleValidator,
            _elmaClient,
            new RealGigaChatClient(options, NullLogger<RealGigaChatClient>.Instance),
            new AverageHashDuplicateDetector(),
            new WindowsDuplicateResolutionService(),
            _orderSelectionService,
            _fileCorrectionService,
            new WindowsTagReviewService(),
            _repository,
            new ManualNotificationService(notifications),
            new ManualReviewViolationPolicy(),
            monitor,
            NullLogger<FileProcessingOrchestrator>.Instance);
    }

    public IReadOnlyCollection<string> AllowedExtensions => _options.AllowedExtensions
        .Where(static extension => !string.IsNullOrWhiteSpace(extension))
        .Select(static extension => extension.StartsWith('.') ? extension : $".{extension}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _repository.InitializeAsync(cancellationToken);

    public async Task ProcessAsync(string filePath, CancellationToken cancellationToken)
    {
        var currentPath = filePath;
        var orderResolution = await ResolveOrderAsync(currentPath, cancellationToken);
        if (orderResolution.Cancelled)
        {
            _notifications.Report(new ManualProcessingNotification(
                "MediaModule: проверка отменена",
                $"Файл не обрабатывался: {Path.GetFileName(currentPath)}"));
            return;
        }

        var orderData = orderResolution.OrderData;
        if (orderData is not null)
        {
            _elmaClient.Remember(currentPath, orderData);
        }

        var validation = _ruleValidator.Validate(currentPath, orderData);
        while (validation.HasViolations)
        {
            var correction = await TryOfferCorrectionAsync(currentPath, validation, cancellationToken);
            if (correction.Action == FileCorrectionAction.AcceptAndMove &&
                !string.IsNullOrWhiteSpace(correction.CorrectedPath))
            {
                currentPath = correction.CorrectedPath;
                if (orderData is not null)
                {
                    _elmaClient.Remember(currentPath, orderData);
                }

                break;
            }

            if (correction.Action == FileCorrectionAction.BackToOrderSelection)
            {
                orderResolution = await ResolveOrderAsync(currentPath, cancellationToken, forceSelection: true);
                if (orderResolution.Cancelled)
                {
                    _notifications.Report(new ManualProcessingNotification(
                        "MediaModule: проверка отменена",
                        $"Файл не обрабатывался: {Path.GetFileName(currentPath)}"));
                    return;
                }

                orderData = orderResolution.OrderData;
                if (orderData is not null)
                {
                    _elmaClient.Remember(currentPath, orderData);
                }

                validation = _ruleValidator.Validate(currentPath, orderData);
                continue;
            }

            if (correction.Action == FileCorrectionAction.CancelProcessing)
            {
                _notifications.Report(new ManualProcessingNotification(
                    "MediaModule: проверка отменена",
                    $"Файл не обрабатывался: {Path.GetFileName(currentPath)}"));
                return;
            }

            break;
        }

        await _orchestrator.ProcessAsync(
            new FileDetectedEvent(currentPath, WatcherChangeTypes.Created, DateTime.UtcNow),
            cancellationToken);
    }

    private async Task<OrderResolution> ResolveOrderAsync(
        string filePath,
        CancellationToken cancellationToken,
        bool forceSelection = false)
    {
        if (!forceSelection)
        {
            var orderData = await _elmaClient.TryResolveOrderAsync(filePath, cancellationToken);
            if (orderData is not null)
            {
                return new OrderResolution(orderData, Cancelled: false);
            }
        }

        var orders = await _elmaClient.GetOrdersAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return new OrderResolution(null, Cancelled: false);
        }

        var selectedOrder = await _orderSelectionService.SelectOrderAsync(filePath, orders, cancellationToken);
        return selectedOrder is null
            ? new OrderResolution(null, Cancelled: true)
            : new OrderResolution(selectedOrder, Cancelled: false);
    }

    private async Task<CorrectionResolution> TryOfferCorrectionAsync(
        string filePath,
        FileValidationResult validation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(validation.RecommendedDirectory) ||
            string.IsNullOrWhiteSpace(validation.RecommendedFileName) ||
            !File.Exists(filePath))
        {
            return new CorrectionResolution(FileCorrectionAction.None, null);
        }

        var reason = string.Join("; ", validation.FailureReasons);
        var action = await _fileCorrectionService.RequestCorrectionAsync(
            filePath,
            validation.RecommendedDirectory,
            validation.RecommendedFileName,
            reason,
            cancellationToken);

        if (action != FileCorrectionAction.AcceptAndMove)
        {
            return new CorrectionResolution(action, null);
        }

        Directory.CreateDirectory(validation.RecommendedDirectory);
        var targetPath = BuildAvailableTargetPath(
            validation.RecommendedDirectory,
            validation.RecommendedFileName);

        if (PathsEqual(filePath, targetPath))
        {
            return new CorrectionResolution(action, filePath);
        }

        File.Move(filePath, targetPath);
        return new CorrectionResolution(action, targetPath);
    }

    private static string BuildAvailableTargetPath(string directory, string fileName)
    {
        var targetPath = Path.Combine(directory, fileName);
        if (!File.Exists(targetPath))
        {
            return targetPath;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var baseName = name;
        var index = 1;
        var separatorIndex = name.LastIndexOf('_');
        if (separatorIndex > 0 &&
            separatorIndex < name.Length - 1 &&
            int.TryParse(name[(separatorIndex + 1)..], out var parsedVersion))
        {
            baseName = name[..separatorIndex];
            index = parsedVersion + 1;
        }

        var candidate = Path.Combine(directory, $"{baseName}_{index}{extension}");
        while (File.Exists(candidate))
        {
            index++;
            candidate = Path.Combine(directory, $"{baseName}_{index}{extension}");
        }

        return candidate;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ManualNotificationService : IFileNotificationService
    {
        private readonly IProgress<ManualProcessingNotification> _notifications;

        public ManualNotificationService(IProgress<ManualProcessingNotification> notifications)
        {
            _notifications = notifications;
        }

        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _notifications.Report(new ManualProcessingNotification(title, message));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ManualReviewViolationPolicy : IViolationPolicy
    {
        public ViolationDecision RegisterViolation(string filePath) =>
            new(ShouldBlock: false, IsIgnored: true, AttemptNumber: 1);

        public void Reset(string filePath)
        {
        }
    }

    private sealed class ManualOrderContextElmaClient : IElmaClient
    {
        private readonly IElmaClient _inner;
        private readonly ConcurrentDictionary<string, SelectedOrderContext> _selectedOrders = new(StringComparer.OrdinalIgnoreCase);

        public ManualOrderContextElmaClient(IElmaClient inner)
        {
            _inner = inner;
        }

        public void Remember(string filePath, OrderData orderData)
        {
            var fingerprint = FileProcessingFingerprint.TryCreate(filePath);
            if (fingerprint is not null)
            {
                _selectedOrders[filePath] = new SelectedOrderContext(orderData, fingerprint);
            }
        }

        public async Task<OrderData?> TryResolveOrderAsync(string filePath, CancellationToken cancellationToken)
        {
            var fingerprint = FileProcessingFingerprint.TryCreate(filePath);
            if (fingerprint is not null &&
                _selectedOrders.TryGetValue(filePath, out var orderContext) &&
                orderContext.Fingerprint.Equals(fingerprint))
            {
                return orderContext.OrderData;
            }

            return await _inner.TryResolveOrderAsync(filePath, cancellationToken);
        }

        public Task<IReadOnlyCollection<OrderData>> GetOrdersAsync(CancellationToken cancellationToken) =>
            _inner.GetOrdersAsync(cancellationToken);

        private sealed record FileProcessingFingerprint(long Length, long LastWriteTimeUtcTicks)
        {
            public static FileProcessingFingerprint? TryCreate(string filePath)
            {
                try
                {
                    var info = new FileInfo(filePath);
                    return info.Exists
                        ? new FileProcessingFingerprint(info.Length, info.LastWriteTimeUtc.Ticks)
                        : null;
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        private sealed record SelectedOrderContext(OrderData OrderData, FileProcessingFingerprint Fingerprint);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record OrderResolution(OrderData? OrderData, bool Cancelled);

    private sealed record CorrectionResolution(FileCorrectionAction Action, string? CorrectedPath);
}

public sealed record ManualProcessingNotification(string Title, string Message);
