# Функции MediaModule

Документ описывает основные функции модуля и где они реализованы в коде.

## Запуск фонового модуля

В эксплуатационном варианте фоновая часть системы поставляется как отдельный исполняемый файл `MediaModule.Worker.exe`. После запуска worker работает без отдельного консольного окна, подписывается на события файловой системы, обрабатывает новые изображения и показывает пользователю системные уведомления.

Desktop-приложение используется для просмотра журнала, настройки правил и ручной проверки архивных файлов, а worker отвечает за постоянный фоновый контроль сохранений. При разработке worker можно запускать из терминала командой `dotnet run --project src/MediaModule.Worker`, но для пользователя модуль рассматривается как готовый `.exe`.

## 1. Отслеживание новых файлов

Модуль следит за рабочими папками дизайнеров и реагирует на появление или изменение графических файлов.

Где реализовано:
- `src/MediaModule.Worker/Worker.cs` — запуск и остановка фонового обработчика.
- `src/MediaModule.Infrastructure/Services/FileSystemEventSource.cs` — события файловой системы.
- `src/MediaModule.Worker/appsettings.json` — список отслеживаемых папок и разрешенных расширений.

## 2. Проверка заказа

Перед обработкой файл связывается с заказом: автоматически через CRM/mini-CRM или вручную через окно выбора заказа.

Где реализовано:
- `src/MediaModule.Application/Abstractions/IElmaClient.cs` — общий интерфейс CRM-клиента.
- `src/MediaModule.Infrastructure/Integration/MockElmaClient.cs` — тестовые заказы из настроек.
- `src/MediaModule.Infrastructure/Services/WindowsOrderSelectionService.cs` — окно выбора заказа.

## 3. Проверка имени и папки

Модуль проверяет, что файл назван по правилам и лежит в структуре:

```text
[Root]\[ClientName]\[ProductType]\
```

Рекомендованное имя первой версии не содержит номер:

```text
Ivanov_banner_2026.png
```

Если такой файл уже есть, следующая версия получает номер:

```text
Ivanov_banner_2026_1.png
Ivanov_banner_2026_2.png
```

Где реализовано:
- `src/MediaModule.Infrastructure/Validation/RegexFileRuleValidator.cs` — проверка имени и пути.
- `src/MediaModule.Application/Services/FileProcessingOrchestrator.cs` — применение исправления и подбор свободного имени.
- `src/MediaModule.Desktop/MainWindow.xaml` — настройки шаблона имени и корневой папки.

## 4. Реакция на нарушение правил

Если файл сохранен неправильно, модуль показывает окно рекомендации. Пользователь может исправить и перенести файл, вернуться к выбору заказа, оставить как есть или отменить проверку.

Где реализовано:
- `src/MediaModule.Infrastructure/Services/WindowsFileCorrectionService.cs` — окно рекомендации.
- `src/MediaModule.Domain/Entities/FileCorrectionAction.cs` — варианты действий пользователя.
- `src/MediaModule.Application/Services/InMemoryViolationPolicy.cs` — политика первой и повторной попытки.

## 5. Поиск дубликатов

Во время обработки модуль считает визуальный хеш изображения и сравнивает его с хешами в локальной SQLite-базе. Если похожий файл найден, открывается окно выбора действия.

Где реализовано:
- `src/MediaModule.Infrastructure/Services/AverageHashDuplicateDetector.cs` — расчет perceptual hash.
- `src/MediaModule.Infrastructure/Persistence/SqliteModuleRepository.cs` — хранение и поиск хешей.
- `src/MediaModule.Infrastructure/Services/WindowsDuplicateResolutionService.cs` — окно решения по дубликату.

## 6. Тегирование через GigaChat

После успешной проверки модуль получает описание и поисковые теги для изображения. Теги можно принять автоматически или подтвердить через окно.

Где реализовано:
- `src/MediaModule.Application/Abstractions/IGigaChatClient.cs` — интерфейс клиента.
- `src/MediaModule.Infrastructure/Integration/RealGigaChatClient.cs` — реальный клиент GigaChat.
- `src/MediaModule.Infrastructure/Integration/MockGigaChatClient.cs` — fallback для тестов.
- `src/MediaModule.Infrastructure/Services/WindowsTagReviewService.cs` — окно подтверждения тегов.

## 7. Журнал операций

Каждый результат обработки сохраняется в SQLite: имя файла, путь, результат, заказ, дубликат и теги.

Где реализовано:
- `src/MediaModule.Infrastructure/Persistence/SqliteModuleRepository.cs` — таблицы и запись данных.
- `src/MediaModule.Desktop/Services/LogQueryService.cs` — чтение журнала для интерфейса.
- `src/MediaModule.Desktop/MainWindow.xaml` — экран журнала.

## 8. Ручная проверка архивных файлов

Для файлов, которые были созданы до внедрения модуля, в Desktop есть раздел проверки файлов. Можно добавить отдельные файлы или папку и запустить ту же обработку, что используется для новых сохранений.

Где реализовано:
- `src/MediaModule.Desktop/Services/ManualFileProcessingService.cs` — запуск ручной обработки.
- `src/MediaModule.Desktop/MainWindow.xaml.cs` — очередь файлов, прогресс и предпросмотр.

## 9. Поиск по тегам

Поиск в верхней строке Desktop ищет по файлам, сообщениям, заказам и сохраненным тегам. Результат показывает найденные файлы и подсвечивает совпадения.

Где реализовано:
- `src/MediaModule.Desktop/Services/LogQueryService.cs` — логика поиска.
- `src/MediaModule.Desktop/MainWindow.xaml.cs` — экран результатов и подсветка.

## 10. Настройки

Администратор может менять корневую папку, шаблон имени, правила проверки, дубликаты и параметры GigaChat. Настройки сохраняются в `appsettings.json` worker-модуля.

Где реализовано:
- `src/MediaModule.Desktop/Services/WorkerSettingsService.cs` — чтение и запись настроек.
- `src/MediaModule.Desktop/MainWindow.xaml` — экран настроек.
- `src/MediaModule.Worker/appsettings.json` — рабочая конфигурация.
