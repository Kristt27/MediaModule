using System.Text.RegularExpressions;
using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Domain.Entities;
using MediaModule.Infrastructure.PathResolution;
using Microsoft.Extensions.Options;

namespace MediaModule.Infrastructure.Validation;

public sealed class RegexFileRuleValidator : IFileRuleValidator
{
    private readonly IOptionsMonitor<ModuleOptions> _options;

    public RegexFileRuleValidator(IOptionsMonitor<ModuleOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Проверяет соответствие файла правилам именования и размещения в каталогах.
    /// </summary>
    public FileValidationResult Validate(string filePath, OrderData? orderData)
    {
        var options = _options.CurrentValue;
        var fileName = Path.GetFileName(filePath);
        string? regexError = null;
        var isNameValid = !options.ValidateFileName || IsFileNameValid(fileName, options.FileNameRegexPattern, out regexError);
        var recommendedDirectory = BuildRecommendedDirectory(orderData, options);
        var recommendedFileName = BuildRecommendedFileName(filePath, orderData, isNameValid);

        var reasons = new List<string>();
        if (options.ValidateFileName && !isNameValid)
        {
            reasons.Add(string.IsNullOrWhiteSpace(regexError)
                ? "Некорректное имя файла относительно regex-шаблона."
                : $"Некорректный regex-шаблон в настройках: {regexError}");
        }

        // Отдельно проверяем, что файл лежит в разрешенной структуре директорий.
        var pathReason = string.Empty;
        var isPathValid = !options.ValidatePath || ValidatePath(filePath, orderData, options, out pathReason);
        if (options.ValidatePath && !isPathValid && !string.IsNullOrWhiteSpace(pathReason))
        {
            reasons.Add(pathReason);
        }

        if (isNameValid && isPathValid)
        {
            return FileValidationResult.Success(recommendedDirectory, recommendedFileName);
        }

        return FileValidationResult.Failed(isNameValid, isPathValid, recommendedDirectory, recommendedFileName, reasons.ToArray());
    }

    private static bool IsFileNameValid(string fileName, string pattern, out string? regexError)
    {
        try
        {
            regexError = null;
            return Regex.IsMatch(fileName, pattern, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            regexError = ex.Message;
            return false;
        }
    }

    private static bool ValidatePath(string filePath, OrderData? orderData, ModuleOptions options, out string reason)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var normalizedDirectory = Path.GetFullPath(directory);
        var root = ModulePathResolver.Resolve(options.RootDirectory);

        // Файл должен находиться внутри корневого каталога, который контролирует модуль.
        if (!normalizedDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Файл сохранен вне корневого каталога модуля.";
            return false;
        }

        if (orderData is null)
        {
            reason = string.Empty;
            return true;
        }

        var expectedDirectory = Path.Combine(root, Sanitize(orderData.ClientName), Sanitize(orderData.ProductType));
        var normalizedExpected = Path.GetFullPath(expectedDirectory);

        // Если заказ найден, дополнительно сверяем путь с ожидаемой папкой клиента и типа продукта.
        if (!normalizedDirectory.StartsWith(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Ожидаемый путь: {normalizedExpected}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string BuildRecommendedDirectory(OrderData? orderData, ModuleOptions options)
    {
        var root = ModulePathResolver.Resolve(options.RootDirectory);
        return orderData is null
            ? root
            : Path.Combine(root, Sanitize(orderData.ClientName), Sanitize(orderData.ProductType));
    }

    private string BuildRecommendedFileName(string filePath, OrderData? orderData, bool isNameValid)
    {
        if (isNameValid)
        {
            return Path.GetFileName(filePath);
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        return orderData is null
            ? $"Client_Product_{DateTime.Now:yyyy}_1{extension}"
            : $"{Sanitize(orderData.ClientName)}_{Sanitize(orderData.ProductType)}_{DateTime.Now:yyyy}_1{extension}";
    }

    private static string Sanitize(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join(string.Empty, value.Where(x => !invalidChars.Contains(x))).Trim();
    }
}
