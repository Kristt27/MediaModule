# MediaModule MVP

Стартовый каркас дипломного модуля управления графическими файлами по требованиям из документов (`ФТ-1..ФТ-16`, `БП`, `СД`).

## Что уже реализовано

- Фоновый сервис на `C#/.NET` для Windows.
- Мониторинг файловой системы через `FileSystemWatcher`.
- Проверка имени файла по `regex`.
- Проверка пути сохранения по структуре `[Root]\[ClientName]\[ProductType]\`.
- Политика нарушений: первая попытка блокируется переносом файла в служебную папку отклоненных сохранений, повторная разрешается с фиксацией игнорирования.


- Мини-CRM для выбора и определения заказа, совместимая с будущей интеграцией `CRM ELMA`.
- Реальный клиент `GigaChat` с fallback на mock-тегирование.
- Логирование в `Serilog` + хранение журнала/хешей/тегов в `SQLite`.
- Desktop-интерфейс (WPF):
  - просмотр журнала обработки,
  - просмотр тегов выбранной записи,
  - умный поиск по файлам и ИИ-тегам,
  - редактирование `appsettings` worker-модуля,
  - `GigaChat Playground` для теста тегов в mock-режиме.

## Структура

- `src/MediaModule.Domain` — сущности и модели домена.
- `src/MediaModule.Application` — интерфейсы и оркестратор обработки.
- `src/MediaModule.Infrastructure` — реализации мониторинга, БД, валидатора, интеграций.
- `src/MediaModule.Worker` — фоновый сервис (Host/DI/конфиг).
- `src/MediaModule.Desktop` — GUI-приложение (WPF).
- `docs/module-functions.md` — описание функций модуля.
- `docs/code-map.md` — карта файлов проекта и основных сценариев.

## Запуск worker

В готовой поставке фоновый модуль запускается как `MediaModule.Worker.exe` и работает без консольного окна. Запуск через `dotnet run` ниже используется для разработки и проверки проекта из исходного кода.

1. Изменить `Module.RootDirectory` и `Module.MonitoredDirectories` в `src/MediaModule.Worker/appsettings.json`.
2. Убедиться, что доступ к CRM ELMA настроен в `Module.Elma`.
3. Для подключения ELMA указать параметры приложения в `Module.Elma`:

```json
"Elma": {
  "Enabled": true,
  "BaseUrl": "https://your-company.elma365.ru",
  "Namespace": "crm",
  "AppCode": "orders",
  "RequestMethod": "POST"
}
```

Токен хранить локально, например в переменной окружения:

```powershell
[Environment]::SetEnvironmentVariable("ELMA_API_TOKEN", "ВАШ_ТОКЕН", "User")
```

Имена полей заказа настраиваются через `OrderIdField`, `ClientNameField`, `ProductTypeField`.
Если ELMA недоступна или параметры не заполнены, пользователь вводит сведения о заказе вручную.
4. Добавить ключ GigaChat в `src/MediaModule.Worker/appsettings.json`:

```json
"GigaChat": {
  "Enabled": true,
  "AuthorizationKey": "ВАШ_КЛЮЧ"
}
```

Если `AuthorizationKey` оставлен пустым, worker попробует взять ключ из переменной окружения `GIGACHAT_AUTHORIZATION_KEY`.
5. Запустить:

```bash
dotnet run --project src/MediaModule.Worker
```

## Запуск desktop UI

```bash
dotnet run --project src/MediaModule.Desktop
```

UI читает/пишет настройки в `src/MediaModule.Worker/appsettings.json` и отображает журнал из `SQLite`.

## Важные ограничения MVP

- ELMA подключается через публичный API приложения; если заказ недоступен, пользователь вводит данные вручную.
- В desktop вкладка `GigaChat Playground` пока имитирует результат тегирования.
- Блокировка «первой попытки» реализована на уровне фонового контроля сохранения: файл переносится из неправильной папки в служебное хранилище отклоненных сохранений.

## Следующий этап

- Расширение маппинга ELMA под фактические поля рабочего приложения.
- Подтверждение/отклонение тегов в GUI с отдельной таблицей pending-тегов.
- Очистка журнала старше 1 года с предупреждением.
- Поиск дубликатов по perceptual hash (с fallback на SHA256).
- Фиксация найденных дубликатов в журнале без изменения имени исходного файла.
