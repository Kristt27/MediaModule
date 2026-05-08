using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaModule.Application.Configuration;

namespace MediaModule.Desktop.Services;

public sealed class WorkerSettingsService
{
    private readonly string _settingsPath;

    public WorkerSettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public bool Exists() => File.Exists(_settingsPath);

    public string SettingsPath => _settingsPath;

    /// <summary>
    /// Читает текущую конфигурацию worker-модуля и преобразует ее
    /// в удобный для экрана настроек объект-снимок.
    /// </summary>
    public WorkerSettingsSnapshot Load()
    {
        if (!File.Exists(_settingsPath))
        {
            throw new FileNotFoundException("Не найден appsettings.json worker-проекта", _settingsPath);
        }

        // Загружаем корневой JSON и извлекаем раздел Module со всеми рабочими параметрами.
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath))?.AsObject() ?? new JsonObject();
        var module = root["Module"]?.AsObject() ?? new JsonObject();
        var miniCrm = module["MiniCrm"]?.AsObject() ?? new JsonObject();
        var elmaMock = module["ElmaMock"]?.AsObject() ?? new JsonObject();
        var gigaChat = module["GigaChat"]?.AsObject() ?? new JsonObject();
        var orders = miniCrm["Orders"]?.AsArray() ?? elmaMock["Orders"]?.AsArray() ?? new JsonArray();

        // Списки директорий и тестовых заказов преобразуются в многострочный текст для редактирования в UI.
        var monitoredDirectories = module["MonitoredDirectories"]?.AsArray() ?? new JsonArray();
        var monitoredLines = monitoredDirectories.Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x));

        var orderLines = orders
            .Select(x => x as JsonObject)
            .Where(x => x is not null)
            .Select(x => $"{x!["OrderId"]};{x["ClientName"]};{x["ProductType"]}");

        var databasePath = module["DatabasePath"]?.ToString() ?? "data/module.db";

        return new WorkerSettingsSnapshot
        {
            RootDirectory = module["RootDirectory"]?.ToString() ?? string.Empty,
            FileNameRegexPattern = module["FileNameRegexPattern"]?.ToString() ?? string.Empty,
            ValidateFileName = module["ValidateFileName"]?.GetValue<bool>() ?? true,
            ValidatePath = module["ValidatePath"]?.GetValue<bool>() ?? true,
            DetectDuplicates = module["DetectDuplicates"]?.GetValue<bool>() ?? true,
            MonitoredDirectoriesMultiline = string.Join(Environment.NewLine, monitoredLines),
            AutoAcceptTags = module["AutoAcceptTags"]?.GetValue<bool>() ?? false,
            DatabasePath = databasePath,
            OrdersMultiline = string.Join(Environment.NewLine, orderLines),
            ResolvedDatabasePath = ResolveDatabasePath(databasePath),
            GigaChatEnabled = gigaChat["Enabled"]?.GetValue<bool>() ?? false,
            GigaChatAuthorizationKey = gigaChat["AuthorizationKey"]?.ToString() ?? string.Empty,
            GigaChatScope = gigaChat["Scope"]?.ToString() ?? "GIGACHAT_API_PERS",
            GigaChatOAuthUrl = gigaChat["OAuthUrl"]?.ToString() ?? "https://ngw.devices.sberbank.ru:9443/api/v2/oauth",
            GigaChatApiBaseUrl = gigaChat["ApiBaseUrl"]?.ToString() ?? "https://gigachat.devices.sberbank.ru/api/v1",
            GigaChatModel = gigaChat["Model"]?.ToString() ?? "GigaChat-Pro",
            GigaChatIgnoreSslCertificateErrors = gigaChat["IgnoreSslCertificateErrors"]?.GetValue<bool>() ?? false,
        };
    }

    public ModuleOptions LoadModuleOptions()
    {
        if (!File.Exists(_settingsPath))
        {
            throw new FileNotFoundException("Не найден appsettings.json worker-проекта", _settingsPath);
        }

        var root = JsonNode.Parse(File.ReadAllText(_settingsPath))?.AsObject() ?? new JsonObject();
        var module = root["Module"];
        var options = module?.Deserialize<ModuleOptions>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new ModuleOptions();

        options.DatabasePath = ResolveDatabasePath(options.DatabasePath);
        return options;
    }

    /// <summary>
    /// Сохраняет изменения из окна настроек обратно в appsettings.json worker-модуля.
    /// </summary>
    public void Save(WorkerSettingsSnapshot snapshot)
    {
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath))?.AsObject() ?? new JsonObject();
        var module = root["Module"]?.AsObject() ?? new JsonObject();
        root["Module"] = module;

        // Обновляем простые скалярные параметры модуля
        module["RootDirectory"] = snapshot.RootDirectory;
        module["FileNameRegexPattern"] = snapshot.FileNameRegexPattern;
        module["ValidateFileName"] = snapshot.ValidateFileName;
        module["ValidatePath"] = snapshot.ValidatePath;
        module["DetectDuplicates"] = snapshot.DetectDuplicates;
        module["AutoAcceptTags"] = snapshot.AutoAcceptTags;
        module["DatabasePath"] = snapshot.DatabasePath;

        // пересобираем список отслеживаемых директорий из многострочного поля интерфейса
        var monitored = new JsonArray();
        foreach (var line in SplitLines(snapshot.MonitoredDirectoriesMultiline))
        {
            monitored.Add(line);
        }

        module["MonitoredDirectories"] = monitored;

        var elmaMock = module["ElmaMock"]?.AsObject() ?? new JsonObject();
        module["ElmaMock"] = elmaMock;
        var miniCrm = module["MiniCrm"]?.AsObject() ?? new JsonObject();
        module["MiniCrm"] = miniCrm;

        // Каждая строка заказов интерпретируется как "OrderId;ClientName;ProductType".
        var orders = new JsonArray();
        foreach (var line in SplitLines(snapshot.OrdersMultiline))
        {
            var parts = line.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            orders.Add(new JsonObject
            {
                ["OrderId"] = parts[0],
                ["ClientName"] = parts[1],
                ["ProductType"] = parts[2],
            });
        }

        elmaMock["Orders"] = DeepCloneOrders(orders);
        miniCrm["Orders"] = orders;

        var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });

        // Перезаписываем исходный конфиг и существующие runtime-копии, которые читает уже собранный worker.
        foreach (var path in GetWritableSettingsPaths())
        {
            File.WriteAllText(path, json);
        }
    }

    public string ResolveDatabasePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var settingsDir = Path.GetDirectoryName(_settingsPath) ?? AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(settingsDir, configuredPath),
            Path.Combine(settingsDir, "bin", "Debug", "net8.0-windows", configuredPath),
            Path.Combine(settingsDir, "bin", "Release", "net8.0-windows", configuredPath),
            Path.Combine(AppContext.BaseDirectory, configuredPath),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static JsonArray DeepCloneOrders(JsonArray source)
    {
        var clone = new JsonArray();
        foreach (var item in source)
        {
            if (item is null)
            {
                continue;
            }

            clone.Add(JsonNode.Parse(item.ToJsonString()));
        }

        return clone;
    }

    private IEnumerable<string> GetWritableSettingsPaths()
    {
        yield return _settingsPath;

        var settingsDir = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(settingsDir))
        {
            yield break;
        }

        var runtimeCandidates = new[]
        {
            Path.Combine(settingsDir, "bin", "Debug", "net8.0-windows", "appsettings.json"),
            Path.Combine(settingsDir, "bin", "Release", "net8.0-windows", "appsettings.json"),
        };

        foreach (var candidate in runtimeCandidates)
        {
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }
}

public sealed class WorkerSettingsSnapshot
{
    public string RootDirectory { get; set; } = string.Empty;

    public string FileNameRegexPattern { get; set; } = string.Empty;

    public bool ValidateFileName { get; set; }

    public bool ValidatePath { get; set; }

    public bool DetectDuplicates { get; set; }

    public string MonitoredDirectoriesMultiline { get; set; } = string.Empty;

    public bool AutoAcceptTags { get; set; }

    public string DatabasePath { get; set; } = string.Empty;

    public string OrdersMultiline { get; set; } = string.Empty;

    public string ResolvedDatabasePath { get; set; } = string.Empty;

    public bool GigaChatEnabled { get; set; }

    public string GigaChatAuthorizationKey { get; set; } = string.Empty;

    public string GigaChatScope { get; set; } = string.Empty;

    public string GigaChatOAuthUrl { get; set; } = string.Empty;

    public string GigaChatApiBaseUrl { get; set; } = string.Empty;

    public string GigaChatModel { get; set; } = string.Empty;

    public bool GigaChatIgnoreSslCertificateErrors { get; set; }
}
