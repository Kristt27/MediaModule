using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using System.Collections.Concurrent;
using MediaModule.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaModule.Application.Services;

/// <summary>
/// Р¤Р°СЃР°Рґ РїСЂРёРєР»Р°РґРЅРѕРіРѕ СЃР»РѕСЏ: СЃРѕР±РёСЂР°РµС‚ РїРѕР»РЅС‹Р№ СЃС†РµРЅР°СЂРёР№ РѕР±СЂР°Р±РѕС‚РєРё С„Р°Р№Р»Р°
/// РёР· РѕС‚РґРµР»СЊРЅС‹С… СЃРµСЂРІРёСЃРѕРІ РёРЅС„СЂР°СЃС‚СЂСѓРєС‚СѓСЂС‹ Рё РґРѕРјРµРЅРЅРѕР№ Р»РѕРіРёРєРё.
/// </summary>
public sealed class FileProcessingOrchestrator
{
    private readonly IFileRuleValidator _ruleValidator;
    private readonly IElmaClient _elmaClient;
    private readonly IGigaChatClient _gigaChatClient;
    private readonly IDuplicateDetector _duplicateDetector;
    private readonly IDuplicateResolutionService _duplicateResolutionService;
    private readonly IOrderSelectionService _orderSelectionService;
    private readonly IFileCorrectionService _fileCorrectionService;
    private readonly ITagReviewService _tagReviewService;
    private readonly IModuleRepository _repository;
    private readonly IFileNotificationService _notificationService;
    private readonly IViolationPolicy _violationPolicy;
    private readonly IOptionsMonitor<ModuleOptions> _options;
    private readonly ILogger<FileProcessingOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, SelectedOrderContext> _selectedOrders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileProcessingFingerprint> _activeFileEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileProcessingFingerprint> _processedFileEvents = new(StringComparer.OrdinalIgnoreCase);

    public FileProcessingOrchestrator(
        IFileRuleValidator ruleValidator,
        IElmaClient elmaClient,
        IGigaChatClient gigaChatClient,
        IDuplicateDetector duplicateDetector,
        IDuplicateResolutionService duplicateResolutionService,
        IOrderSelectionService orderSelectionService,
        IFileCorrectionService fileCorrectionService,
        ITagReviewService tagReviewService,
        IModuleRepository repository,
        IFileNotificationService notificationService,
        IViolationPolicy violationPolicy,
        IOptionsMonitor<ModuleOptions> options,
        ILogger<FileProcessingOrchestrator> logger)
    {
        _ruleValidator = ruleValidator;
        _elmaClient = elmaClient;
        _gigaChatClient = gigaChatClient;
        _duplicateDetector = duplicateDetector;
        _duplicateResolutionService = duplicateResolutionService;
        _orderSelectionService = orderSelectionService;
        _fileCorrectionService = fileCorrectionService;
        _tagReviewService = tagReviewService;
        _repository = repository;
        _notificationService = notificationService;
        _violationPolicy = violationPolicy;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Р’С‹РїРѕР»РЅСЏРµС‚ РїРѕР»РЅС‹Р№ РєРѕРЅРІРµР№РµСЂ РѕР±СЂР°Р±РѕС‚РєРё: РїСЂРѕРІРµСЂРєР° РїСЂР°РІРёР», РїРѕР»РёС‚РёРєР° РЅР°СЂСѓС€РµРЅРёР№,
    /// РїРѕРёСЃРє РґСѓР±Р»РёРєР°С‚РѕРІ, РіРµРЅРµСЂР°С†РёСЏ С‚РµРіРѕРІ Рё Р·Р°РїРёСЃСЊ СЂРµР·СѓР»СЊС‚Р°С‚Р° РІ Р¶СѓСЂРЅР°Р».
    /// </summary>
    public async Task ProcessAsync(FileDetectedEvent fileEvent, CancellationToken cancellationToken)
    {
        var path = fileEvent.FullPath;

        if (!File.Exists(path))
        {
            return;
        }

        if (!IsAllowedExtension(path))
        {
            return;
        }

        await WaitForFileReadyAsync(path, cancellationToken);
        var originalFingerprint = FileProcessingFingerprint.TryCreate(path);
        if (originalFingerprint is null || !TryBeginFileEvent(path, originalFingerprint))
        {
            return;
        }

        try
        {
            _logger.LogInformation("Начата обработка файла: {Path} ({ChangeType})", path, fileEvent.ChangeType);
            await _notificationService.NotifyAsync(
                "MediaModule: подождите",
                $"Файл сохранен, начинаю проверку:\n{Path.GetFileName(path)}",
                cancellationToken);

            // РЎРЅР°С‡Р°Р»Р° РїРѕРґС‚СЏРіРёРІР°РµРј Р±РёР·РЅРµСЃ-РєРѕРЅС‚РµРєСЃС‚ Р·Р°РєР°Р·Р° Рё РїСЂРѕРІРµСЂСЏРµРј РїСЂР°РІРёР»Р° РёРјРµРЅРё/РїСѓС‚Рё.
            var orderData = await ResolveOrderAsync(path, originalFingerprint, cancellationToken);
            var currentPath = path;
            var validation = _ruleValidator.Validate(currentPath, orderData);
            var hasViolation = validation.HasViolations;
            var violationMessage = string.Join("; ", validation.FailureReasons);
            var recommendation = BuildRecommendation(currentPath, validation);
            var errorIgnored = false;
            var violationKey = BuildViolationKey(path, originalFingerprint);

            if (hasViolation)
            {
                // РџРѕР»РёС‚РёРєР° РЅР°СЂСѓС€РµРЅРёР№ СѓРїСЂР°РІР»СЏРµС‚ СЃС†РµРЅР°СЂРёРµРј "РїРµСЂРІР°СЏ РїРѕРїС‹С‚РєР° Р±Р»РѕРєРёСЂСѓРµС‚СЃСЏ, РїРѕРІС‚РѕСЂРЅР°СЏ СЂР°Р·СЂРµС€Р°РµС‚СЃСЏ".
                var decision = _violationPolicy.RegisterViolation(violationKey);

                if (decision.ShouldBlock)
                {
                    var rejectedPath = await TryMoveRejectedFileAsync(path, cancellationToken);
                    var rollbackMessage = rejectedPath is not null
                        ? $" Файл перенесен в служебную папку отклоненных сохранений: {rejectedPath}."
                        : " Сохранение помечено как запрещенное, но файл не удалось перенести автоматически.";
                    var message = $"Сохранение запрещено: {violationMessage}{recommendation}{rollbackMessage}";
                    var notificationMessage = BuildViolationNotification(validation, violationMessage, rejectedPath is not null);

                    await _notificationService.NotifyAsync("MediaModule: проверка сохранения", notificationMessage, cancellationToken);
                    var correctedPath = await TryApplyUserCorrectionAsync(
                        rejectedPath,
                        validation,
                        violationMessage,
                        cancellationToken);

                    if (correctedPath is not null)
                    {
                        currentPath = correctedPath;
                        hasViolation = false;
                        errorIgnored = false;
                        _violationPolicy.Reset(violationKey);
                        await _notificationService.NotifyAsync(
                            "MediaModule: исправление принято",
                            $"Файл перемещен, продолжаю проверку и тегирование:\n{Path.GetFileName(correctedPath)}",
                            cancellationToken);
                    }
                    else
                    {
                        await SaveLogAsync(rejectedPath ?? path, ProcessingResult.Blocked, false, message, orderData, orderData?.OrderId, null, Array.Empty<TagItem>(), cancellationToken);
                        return;
                    }
                }

                if (hasViolation)
                {
                    errorIgnored = decision.IsIgnored;
                    await _notificationService.NotifyAsync(
                        "MediaModule: нарушение проигнорировано",
                        $"Повторная попытка разрешена.\n{BuildViolationNotification(validation, violationMessage, false)}",
                        cancellationToken);
                }
            }
            else
            {
                _violationPolicy.Reset(violationKey);
            }

            string? duplicateOf = null;
            var processingMessage = "OK";

            // РҐРµС€ РЅСѓР¶РµРЅ Рё РґР»СЏ РїРѕРёСЃРєР° РєР»РѕРЅР°, Рё РґР»СЏ РїРѕСЃР»РµРґСѓСЋС‰РµРіРѕ СЃРѕС…СЂР°РЅРµРЅРёСЏ С„Р°РєС‚Р° РѕР±СЂР°Р±РѕС‚РєРё.
            await _notificationService.NotifyAsync(
                "MediaModule: проверяю дубликаты",
                $"Сравниваю изображение с журналом:\n{Path.GetFileName(currentPath)}",
                cancellationToken);

            var hash = await _duplicateDetector.ComputePerceptualHashAsync(currentPath, cancellationToken);

            if (_options.CurrentValue.DetectDuplicates)
            {
                duplicateOf = await _repository.FindByHashAsync(hash, currentPath, cancellationToken);

                if (!string.IsNullOrWhiteSpace(duplicateOf))
                {
                    await _notificationService.NotifyAsync(
                        "MediaModule: найден похожий файл",
                        $"Открою окно выбора действия:\n{Path.GetFileName(currentPath)}",
                        cancellationToken);

                    var duplicateAction = await _duplicateResolutionService.ResolveAsync(
                        currentPath,
                        duplicateOf,
                        orderData,
                        cancellationToken);

                    switch (duplicateAction)
                    {
                        case DuplicateResolutionAction.ChooseAnotherOrder:
                            var selectedOrder = await SelectAnotherOrderAsync(currentPath, cancellationToken);
                            if (selectedOrder is not null)
                            {
                                orderData = selectedOrder;
                                _selectedOrders[currentPath] = new SelectedOrderContext(
                                    selectedOrder,
                                    FileProcessingFingerprint.TryCreate(currentPath) ?? originalFingerprint);
                                duplicateOf = null;
                                processingMessage = "Похожий файл найден, выбран другой заказ.";
                            }

                            break;

                        case DuplicateResolutionAction.CancelSave:
                            var rejectedDuplicatePath = await TryMoveRejectedFileAsync(currentPath, cancellationToken);
                            var cancelMessage = $"Сохранение отменено пользователем: похожий файл уже есть в журнале ({duplicateOf}).";
                            await SaveLogAsync(
                                rejectedDuplicatePath ?? currentPath,
                                ProcessingResult.Blocked,
                                false,
                                cancelMessage,
                                orderData,
                                orderData?.OrderId,
                                duplicateOf,
                                Array.Empty<TagItem>(),
                                cancellationToken);
                            await _notificationService.NotifyAsync(
                                "MediaModule: сохранение отменено",
                                rejectedDuplicatePath is null
                                    ? $"Файл оставлен на месте:\n{Path.GetFileName(currentPath)}"
                                    : $"Файл перенесен в rejected:\n{Path.GetFileName(rejectedDuplicatePath)}",
                                cancellationToken);
                            return;

                        case DuplicateResolutionAction.ReplaceExisting:
                            currentPath = ReplaceExistingFile(currentPath, duplicateOf);
                            duplicateOf = null;
                            processingMessage = "Предыдущий похожий файл заменен новым.";
                            await _notificationService.NotifyAsync(
                                "MediaModule: файл заменен",
                                $"Предыдущий похожий файл заменен:\n{Path.GetFileName(currentPath)}",
                                cancellationToken);
                            break;

                        default:
                            await _notificationService.NotifyAsync(
                                "MediaModule: найден дубликат",
                                $"Похожий файл уже есть в журнале:\n{Path.GetFileName(duplicateOf)}",
                                cancellationToken);
                            break;
                    }
                }
            }

            // РЎРѕС…СЂР°РЅСЏРµРј Р°РєС‚СѓР°Р»СЊРЅС‹Р№ РїСѓС‚СЊ С„Р°Р№Р»Р° Рё РµРіРѕ С…РµС€ РІ Р±Р°Р·Рµ, С‡С‚РѕР±С‹ СѓС‡РёС‚С‹РІР°С‚СЊ РµРіРѕ РІ СЃР»РµРґСѓСЋС‰РёС… РїСЂРѕРІРµСЂРєР°С….
            await _repository.UpsertFileHashAsync(currentPath, hash, orderData, cancellationToken);

            IReadOnlyCollection<TagItem> tags = Array.Empty<TagItem>();
            if (File.Exists(currentPath))
            {
                await _notificationService.NotifyAsync(
                    "MediaModule: ожидаю теги GigaChat",
                    $"{BuildTaggingStatusText(hasViolation)} Получаю описание и поисковые теги:\n{Path.GetFileName(currentPath)}",
                    cancellationToken);

                tags = await _gigaChatClient.GenerateTagsAsync(currentPath, orderData, cancellationToken);

                if (tags.Count > 0 && _options.CurrentValue.AutoAcceptTags)
                {
                    await _repository.SaveTagsAsync(currentPath, tags, orderData, cancellationToken);
                    await _notificationService.NotifyAsync(
                        "MediaModule: теги сохранены",
                        $"Принято тегов: {tags.Count}\n{Path.GetFileName(currentPath)}",
                        cancellationToken);
                }
                else if (tags.Count > 0)
                {
                    var accepted = await _tagReviewService.RequestTagApprovalAsync(currentPath, tags, orderData, cancellationToken);
                    if (accepted)
                    {
                        await _repository.SaveTagsAsync(currentPath, tags, orderData, cancellationToken);
                        await _notificationService.NotifyAsync(
                            "MediaModule: теги сохранены",
                            $"Принято тегов: {tags.Count}\n{Path.GetFileName(currentPath)}",
                            cancellationToken);
                    }
                    else
                    {
                        tags = Array.Empty<TagItem>();
                        await _notificationService.NotifyAsync(
                            "MediaModule: теги отклонены",
                            $"Файл сохранен без тегов:\n{Path.GetFileName(currentPath)}",
                            cancellationToken);
                    }
                }
                else
                {
                    await _notificationService.NotifyAsync(
                        "MediaModule: теги не сформированы",
                        $"GigaChat не вернул теги для файла:\n{Path.GetFileName(currentPath)}",
                        cancellationToken);
                }
            }

            var result = hasViolation
                ? ProcessingResult.SavedWithIgnoredViolation
                : duplicateOf is null
                    ? ProcessingResult.Success
                    : ProcessingResult.DuplicateFound;

            // Р¤РёРЅР°Р»СЊРЅС‹Р№ СЌС‚Р°Рї СЃС†РµРЅР°СЂРёСЏ вЂ” Р·Р°РїРёСЃСЊ СЂРµР·СѓР»СЊС‚Р°С‚Р° РѕР±СЂР°Р±РѕС‚РєРё Рё СЃРѕРїСѓС‚СЃС‚РІСѓСЋС‰РёС… РґР°РЅРЅС‹С… РІ Р¶СѓСЂРЅР°Р».
            await SaveLogAsync(
                currentPath,
                result,
                errorIgnored,
                hasViolation ? violationMessage : processingMessage,
                orderData,
                orderData?.OrderId,
                duplicateOf,
                tags,
                cancellationToken);

            _logger.LogInformation("Обработка завершена: {Path} -> {Result}", currentPath, result);
            await _notificationService.NotifyAsync(
                "MediaModule: обработка завершена",
                $"{BuildCompletionMessage(result)}\n{Path.GetFileName(currentPath)}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки файла {Path}", path);
            await SaveLogAsync(path, ProcessingResult.Failed, false, ex.Message, null, null, null, Array.Empty<TagItem>(), cancellationToken);
            await _notificationService.NotifyAsync("MediaModule: ошибка", ex.Message, cancellationToken);
        }
        finally
        {
            CompleteFileEvent(path, originalFingerprint);
        }
    }

    private static string BuildTaggingStatusText(bool hasViolation)
    {
        return hasViolation
            ? "Файл сохранен с проигнорированным нарушением."
            : "Файл прошел проверку.";
    }

    private static string BuildCompletionMessage(ProcessingResult result)
    {
        return result switch
        {
            ProcessingResult.DuplicateFound => "Файл сохранен как похожий на уже существующий.",
            ProcessingResult.SavedWithIgnoredViolation => "Файл обработан с проигнорированным нарушением.",
            ProcessingResult.Success => "Файл проверен, теги и запись журнала обновлены.",
            _ => "Обработка файла завершена.",
        };
    }

    private bool TryBeginFileEvent(string filePath, FileProcessingFingerprint fingerprint)
    {
        if (_processedFileEvents.TryGetValue(filePath, out var processed) && processed.Equals(fingerprint))
        {
            _logger.LogInformation("Повторное событие для уже обработанного состояния файла пропущено: {Path}", filePath);
            return false;
        }

        while (true)
        {
            if (!_activeFileEvents.TryGetValue(filePath, out var active))
            {
                if (_activeFileEvents.TryAdd(filePath, fingerprint))
                {
                    return true;
                }

                continue;
            }

            if (active.Equals(fingerprint))
            {
                _logger.LogInformation("Повторное событие для уже обрабатываемого состояния файла пропущено: {Path}", filePath);
                return false;
            }

            if (_activeFileEvents.TryUpdate(filePath, fingerprint, active))
            {
                return true;
            }
        }
    }

    private void CompleteFileEvent(string filePath, FileProcessingFingerprint fingerprint)
    {
        _processedFileEvents[filePath] = fingerprint;
        _ = ((ICollection<KeyValuePair<string, FileProcessingFingerprint>>)_activeFileEvents)
            .Remove(new KeyValuePair<string, FileProcessingFingerprint>(filePath, fingerprint));
    }

    private static string BuildViolationKey(string filePath, FileProcessingFingerprint fingerprint)
    {
        return $"{filePath}|{fingerprint.Length}|{fingerprint.LastWriteTimeUtcTicks}";
    }

    private bool IsAllowedExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return _options.CurrentValue.AllowedExtensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRecommendation(string filePath, FileValidationResult validation)
    {
        var pathPart = string.IsNullOrWhiteSpace(validation.RecommendedDirectory)
            ? string.Empty
            : $" Рекомендуемая папка: {validation.RecommendedDirectory}.";
        var namePart = string.IsNullOrWhiteSpace(validation.RecommendedFileName)
            ? string.Empty
            : $" Рекомендуемое имя: {validation.RecommendedFileName}.";

        return $" Текущий путь: {filePath}.{pathPart}{namePart}";
    }

    private static string BuildViolationNotification(
        FileValidationResult validation,
        string violationMessage,
        bool movedToRejectedDirectory)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(validation.RecommendedFileName))
        {
            lines.Add($"Имя: {validation.RecommendedFileName}");
        }

        if (!string.IsNullOrWhiteSpace(validation.RecommendedDirectory))
        {
            lines.Add($"Папка: {ShortenPath(validation.RecommendedDirectory)}");
        }

        lines.Add(movedToRejectedDirectory
            ? "Подтвердите исправление в окне модуля."
            : "Повторная попытка записана в журнал.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string ShortenPath(string path)
    {
        const int maxLength = 90;
        if (path.Length <= maxLength)
        {
            return path;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $"{root}...\\{fileName}";
    }

    private async Task<string?> TryMoveRejectedFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForFileReadyAsync(filePath, cancellationToken);
            if (!File.Exists(filePath))
            {
                return filePath;
            }

            var rejectedDirectory = ResolveRejectedDirectory();
            Directory.CreateDirectory(rejectedDirectory);

            var rejectedPath = BuildRejectedFilePath(rejectedDirectory, filePath);
            File.Move(filePath, rejectedPath);
            _logger.LogInformation("Некорректно сохраненный файл перенесен в служебную папку: {Path} -> {RejectedPath}", filePath, rejectedPath);
            return rejectedPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось автоматически перенести некорректно сохраненный файл: {Path}", filePath);
            return null;
        }
    }

    private async Task<string?> TryApplyUserCorrectionAsync(
        string? rejectedPath,
        FileValidationResult validation,
        string violationMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rejectedPath) ||
            string.IsNullOrWhiteSpace(validation.RecommendedDirectory) ||
            string.IsNullOrWhiteSpace(validation.RecommendedFileName) ||
            !File.Exists(rejectedPath))
        {
            return null;
        }

        var action = await _fileCorrectionService.RequestCorrectionAsync(
            rejectedPath,
            validation.RecommendedDirectory,
            validation.RecommendedFileName,
            violationMessage,
            cancellationToken);

        if (action == FileCorrectionAction.None)
        {
            return null;
        }

        Directory.CreateDirectory(validation.RecommendedDirectory);

        var targetPath = BuildAvailableTargetPath(
            validation.RecommendedDirectory,
            validation.RecommendedFileName);

        File.Move(rejectedPath, targetPath);
        return targetPath;
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
        var index = 1;
        var candidate = Path.Combine(directory, $"{name}({index}){extension}");
        while (File.Exists(candidate))
        {
            index++;
            candidate = Path.Combine(directory, $"{name}({index}){extension}");
        }

        return candidate;
    }

    private async Task<OrderData?> ResolveOrderAsync(
        string path,
        FileProcessingFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var orderData = await _elmaClient.TryResolveOrderAsync(path, cancellationToken);
        if (orderData is not null)
        {
            _selectedOrders[path] = new SelectedOrderContext(orderData, fingerprint);
            return orderData;
        }

        if (_selectedOrders.TryGetValue(path, out var cachedOrder) &&
            cachedOrder.Fingerprint.Equals(fingerprint))
        {
            return cachedOrder.OrderData;
        }

        var orders = await _elmaClient.GetOrdersAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return null;
        }

        var selectedOrder = await _orderSelectionService.SelectOrderAsync(path, orders, cancellationToken);
        if (selectedOrder is not null)
        {
            _selectedOrders[path] = new SelectedOrderContext(selectedOrder, fingerprint);
        }

        return selectedOrder;
    }

    private async Task<OrderData?> SelectAnotherOrderAsync(string path, CancellationToken cancellationToken)
    {
        var orders = await _elmaClient.GetOrdersAsync(cancellationToken);
        return orders.Count == 0
            ? null
            : await _orderSelectionService.SelectOrderAsync(path, orders, cancellationToken);
    }

    private static string ReplaceExistingFile(string currentPath, string existingPath)
    {
        if (PathsEqual(currentPath, existingPath))
        {
            return currentPath;
        }

        var existingDirectory = Path.GetDirectoryName(existingPath);
        if (!string.IsNullOrWhiteSpace(existingDirectory))
        {
            Directory.CreateDirectory(existingDirectory);
        }

        File.Copy(currentPath, existingPath, overwrite: true);
        File.Delete(currentPath);
        return existingPath;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveRejectedDirectory()
    {
        var configured = _options.CurrentValue.RejectedFilesDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "rejected";
        }

        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.Combine(AppContext.BaseDirectory, configured);
    }

    private static string BuildRejectedFilePath(string rejectedDirectory, string sourceFilePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        var extension = Path.GetExtension(sourceFilePath);
        var safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = "rejected";
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        var candidate = Path.Combine(rejectedDirectory, $"{safeFileName}_{stamp}{extension}");
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(rejectedDirectory, $"{safeFileName}_{stamp}_{index}{extension}");
            index++;
        }

        return candidate;
    }

    private static async Task WaitForFileReadyAsync(string filePath, CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                await Task.Delay(150, cancellationToken);
                continue;
            }

            try
            {
                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length > 0)
                {
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>
    /// РџСЂРёРІРѕРґРёС‚ РІСЃРµ РёСЃС…РѕРґС‹ РѕР±СЂР°Р±РѕС‚РєРё Рє РµРґРёРЅРѕРјСѓ С„РѕСЂРјР°С‚Сѓ Р·Р°РїРёСЃРё РІ Р¶СѓСЂРЅР°Р».
    /// </summary>
    private async Task SaveLogAsync(
        string filePath,
        ProcessingResult result,
        bool errorIgnored,
        string message,
        OrderData? orderData,
        string? orderId,
        string? duplicateOf,
        IReadOnlyCollection<TagItem> tags,
        CancellationToken cancellationToken)
    {
        var entry = new ProcessingLogEntry
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            UserName = Environment.UserName,
            OperationTimeUtc = DateTime.UtcNow,
            Result = result,
            ErrorIgnored = errorIgnored,
            Message = message,
            OrderId = orderId,
            DuplicateOf = duplicateOf,
            Tags = tags,
        };

        await _repository.SaveLogAsync(entry, orderData, cancellationToken);
    }

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
