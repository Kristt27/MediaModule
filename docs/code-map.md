# Карта файлов проекта

Короткая навигация по решению `MediaModule`.

## Слои решения

`src/MediaModule.Domain`

Содержит простые доменные сущности и перечисления:
- `OrderData` — данные заказа;
- `TagItem` — тег изображения;
- `ProcessingLogEntry` — запись журнала;
- `ProcessingResult` — результат обработки;
- `FileCorrectionAction` — действие в окне рекомендации;
- `DuplicateResolutionAction` — действие в окне дубликата.

`src/MediaModule.Application`

Содержит сценарии и интерфейсы приложения:
- `FileProcessingOrchestrator.cs` — главный конвейер обработки файла;
- `IFileRuleValidator.cs` — проверка имени и пути;
- `IElmaClient.cs` — получение заказов;
- `IGigaChatClient.cs` — получение тегов;
- `IDuplicateDetector.cs` — расчет хеша;
- `IModuleRepository.cs` — запись и чтение данных.

`src/MediaModule.Infrastructure`

Содержит реальные реализации:
- `Validation/RegexFileRuleValidator.cs` — проверка имени и папки;
- `Persistence/SqliteModuleRepository.cs` — SQLite база;
- `Services/AverageHashDuplicateDetector.cs` — поиск похожести по visual hash;
- `Services/Windows*Service.cs` — окна WinForms, которые всплывают при обработке;
- `Integration/RealGigaChatClient.cs` — реальный GigaChat;
- `Integration/MockElmaClient.cs` и `MockGigaChatClient.cs` — тестовые интеграции.

`src/MediaModule.Worker`

Фоновый модуль:
- `Program.cs` — настройка зависимостей и запуск host;
- `Worker.cs` — подписка на события файлов;
- `appsettings.json` — рабочие настройки.

`src/MediaModule.Desktop`

WPF-интерфейс:
- `MainWindow.xaml` — верстка экранов;
- `MainWindow.xaml.cs` — логика экранов, навигации, поиска, настроек и ручной проверки;
- `Services/WorkerSettingsService.cs` — чтение и запись настроек worker;
- `Services/ManualFileProcessingService.cs` — ручная обработка архивных файлов;
- `Services/LogQueryService.cs` — чтение журнала и поиск.

`tests/MediaModule.Tests`

Автотесты критичной логики:
- валидация имени и пути;
- политика нарушений;
- поиск дубликатов;
- повторная обработка измененного файла;
- отмена проверки;
- поиск по тегам.

## Основные сценарии

### Новое сохранение файла

1. `FileSystemEventSource` замечает файл.
2. `Worker` передает событие в `FileProcessingOrchestrator`.
3. Оркестратор получает заказ через `IElmaClient`.
4. `RegexFileRuleValidator` проверяет имя и путь.
5. При нарушении открывается `WindowsFileCorrectionService`.
6. `AverageHashDuplicateDetector` считает visual hash.
7. `SqliteModuleRepository` ищет похожие файлы.
8. При дубликате открывается `WindowsDuplicateResolutionService`.
9. `IGigaChatClient` формирует теги.
10. Результат сохраняется в SQLite.

### Ручная проверка старых файлов

1. Пользователь добавляет файлы в Desktop.
2. `ManualFileProcessingService` выбирает заказ и проверяет рекомендации.
3. Файл передается в общий `FileProcessingOrchestrator`.
4. Результат появляется в журнале.

### Просмотр результата

1. `LogQueryService` читает журнал из SQLite.
2. `MainWindow` обновляет дашборд, журнал и поиск.
3. Двойной клик по записи открывает подробности файла.

## Где менять частые вещи

Шаблон имени файла:
- `src/MediaModule.Worker/appsettings.json`
- экран `Настройки` в `src/MediaModule.Desktop/MainWindow.xaml`

Корневая папка хранения:
- `Module.RootDirectory` в `appsettings.json`
- экран `Настройки`

Список заказов для теста:
- `Module.MiniCrm.Orders` или `Module.ElmaMock.Orders` в `appsettings.json`

Порог похожести:
- `Module.DuplicateHashDistanceThreshold` в `appsettings.json`

Разрешенные расширения:
- `Module.AllowedExtensions` в `appsettings.json`
