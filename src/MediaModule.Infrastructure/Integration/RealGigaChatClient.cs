using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaModule.Infrastructure.Integration;

public sealed class RealGigaChatClient : IGigaChatClient
{
    private readonly GigaChatOptions _options;
    private readonly MockGigaChatClient _fallback = new();
    private readonly ILogger<RealGigaChatClient> _logger;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public RealGigaChatClient(IOptions<ModuleOptions> options, ILogger<RealGigaChatClient> logger)
    {
        _options = options.Value.GigaChat;
        ApplyEnvironmentOverrides(_options);
        _logger = logger;
        _httpClient = new HttpClient(CreateHttpClientHandler(_options))
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)),
        };
    }

    public async Task<IReadOnlyCollection<TagItem>> GenerateTagsAsync(
        string filePath,
        OrderData? orderData,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.AuthorizationKey))
        {
            return await _fallback.GenerateTagsAsync(filePath, orderData, cancellationToken);
        }

        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var attachmentId = File.Exists(filePath)
                ? await UploadFileAsync(filePath, token, cancellationToken)
                : null;
            var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("chat/completions"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent(CreatePrompt(filePath, orderData, attachmentId));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            var parsed = EnrichTags(ParseTags(content), filePath, orderData);
            return parsed.Count > 1
                ? parsed
                : await _fallback.GenerateTagsAsync(filePath, orderData, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GigaChat недоступен, используется fallback-тегирование для файла {Path}", filePath);
            return _options.UseMockFallback
                ? await _fallback.GenerateTagsAsync(filePath, orderData, cancellationToken)
                : Array.Empty<TagItem>();
        }
    }

    private static void ApplyEnvironmentOverrides(GigaChatOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AuthorizationKey))
        {
            return;
        }

        var authorizationKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTHORIZATION_KEY");
        if (!string.IsNullOrWhiteSpace(authorizationKey))
        {
            options.AuthorizationKey = authorizationKey.Trim();
            options.Enabled = true;
        }
    }

    private static HttpClientHandler CreateHttpClientHandler(GigaChatOptions options)
    {
        var handler = new HttpClientHandler();
        if (options.IgnoreSslCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.OAuthUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _options.AuthorizationKey);
        request.Headers.Add("RqUID", Guid.NewGuid().ToString());
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["scope"] = _options.Scope,
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        _accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
        var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var expires)
            ? DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64())
            : DateTimeOffset.UtcNow.AddMinutes(25);
        _accessTokenExpiresAt = expiresAt;

        return _accessToken;
    }

    private async Task<string?> UploadFileAsync(string filePath, string token, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(filePath));

        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        form.Add(new StringContent("general"), "purpose");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("files"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return doc.RootElement.TryGetProperty("id", out var id)
            ? id.GetString()
            : doc.RootElement.TryGetProperty("id_", out var altId)
                ? altId.GetString()
                : null;
    }

    private object CreatePrompt(string filePath, OrderData? orderData, string? attachmentId)
    {
        var prompt = string.Join(
            Environment.NewLine,
            "Проанализируй прикрепленный графический файл и связанные с ним данные.",
            $"Имя файла: {Path.GetFileName(filePath)}",
            $"Расширение: {Path.GetExtension(filePath)}",
            $"OrderId: {orderData?.OrderId ?? "не указан"}",
            $"Клиент: {orderData?.ClientName ?? "не указан"}",
            $"Тип продукта: {orderData?.ProductType ?? "не указан"}",
            "Сначала опиши изображение как человек, который ищет макет глазами: что изображено, какие объекты и персонажи есть, какие видимые надписи, основные цвета, фон, композиция, стиль и назначение.",
            "После описания сформируй не меньше 15 поисковых характеристик. Они должны помогать найти макет по визуальной памяти: «фиолетовая памятка с таблицей», «баннер с синей кнопкой», «постер с крупным заголовком».",
            "Обязательные характеристики:",
            "1. visual_description - 1-2 предложения о том, что реально изображено.",
            "2. visible_text - все читаемые надписи через запятую; если текста нет, напиши «без текста».",
            "3. dominant_colors - 2-5 основных цветов простыми словами на русском.",
            "4. background - цвет/тип фона.",
            "5. composition - расположение элементов: сетка, карточки, таблица, центр, две колонки, верхний заголовок и т.п.",
            "6. object_type - тип макета: памятка, баннер, постер, логотип, карточка, презентация, инструкция и т.п.",
            "7. product_type - продукт из заказа или визуально определенный тип.",
            "8. style - визуальный стиль: минимализм, корпоративный, детский, ретро, информационный, яркий и т.п.",
            "9. mood - настроение/тон: строгий, дружелюбный, праздничный, учебный, деловой и т.п.",
            "10. purpose - для чего макет: реклама, инструкция, информирование, брендирование, объявление.",
            "11. audience - кому предназначен макет, если можно понять.",
            "12. format - ориентация и формат: вертикальный/горизонтальный, квадратный, лист, сторис и т.п.",
            "13. client - клиент из заказа, если указан.",
            "14. order_id - номер заказа, если указан.",
            "15. search_keywords - 10-20 слов и фраз через запятую, включая синонимы и русские поисковые запросы.",
            "Если поле уже указано во входных данных, обязательно продублируй его в tags: client, order_id, product_type, file_name, extension.",
            "Не используй значения unknown, generic, «неизвестно» и пустые значения. Если признак нельзя уверенно определить визуально, напиши полезное приближение вроде «информационный макет» или «светлый фон».",
            "Верни строго JSON без markdown и пояснений. Поле tags обязательно должно содержать массив объектов key/value, а не только description. Формат:",
            "{\"description\":\"общее описание\",\"tags\":[{\"key\":\"visual_description\",\"value\":\"...\"},{\"key\":\"visible_text\",\"value\":\"...\"},{\"key\":\"dominant_colors\",\"value\":\"...\"},{\"key\":\"background\",\"value\":\"...\"},{\"key\":\"composition\",\"value\":\"...\"},{\"key\":\"object_type\",\"value\":\"...\"},{\"key\":\"product_type\",\"value\":\"...\"},{\"key\":\"style\",\"value\":\"...\"},{\"key\":\"mood\",\"value\":\"...\"},{\"key\":\"purpose\",\"value\":\"...\"},{\"key\":\"audience\",\"value\":\"...\"},{\"key\":\"format\",\"value\":\"...\"},{\"key\":\"client\",\"value\":\"...\"},{\"key\":\"order_id\",\"value\":\"...\"},{\"key\":\"search_keywords\",\"value\":\"...\"}]}");

        var message = new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = prompt,
        };

        if (!string.IsNullOrWhiteSpace(attachmentId))
        {
            message["attachments"] = new[] { attachmentId };
        }

        return new
        {
            model = _options.Model,
            messages = new[] { message },
            temperature = 0.2,
        };
    }

    private string BuildApiUrl(string relativePath)
    {
        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        return $"{baseUrl}/{relativePath.TrimStart('/')}";
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static IReadOnlyCollection<TagItem> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<TagItem>();
        }

        var jsonObjectTags = ParseJsonObjectTags(raw);
        if (jsonObjectTags.Count > 0)
        {
            return jsonObjectTags;
        }

        var colonTags = ParseColonTags(raw);
        if (colonTags.Count > 0)
        {
            return colonTags;
        }

        var json = ExtractJsonArray(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<TagItem>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<TagItem>();
            }

            return ReadTagArray(doc.RootElement);
        }
        catch
        {
            return Array.Empty<TagItem>();
        }
    }

    private static IReadOnlyCollection<TagItem> ParseJsonObjectTags(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<TagItem>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<TagItem>();
            }

            var tags = new List<TagItem>();
            if (doc.RootElement.TryGetProperty("description", out var descriptionProp))
            {
                var description = descriptionProp.GetString();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    tags.Add(new TagItem("description", description.Trim()));
                }
            }

            if (doc.RootElement.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(ReadTagArray(tagsProp));
            }
            else if (doc.RootElement.TryGetProperty("tags", out tagsProp) && tagsProp.ValueKind == JsonValueKind.Object)
            {
                tags.AddRange(ReadFlatObjectTags(tagsProp));
            }

            tags.AddRange(ReadFlatObjectTags(doc.RootElement));
            return tags
                .GroupBy(static tag => tag.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();
        }
        catch
        {
            return Array.Empty<TagItem>();
        }
    }

    private static IReadOnlyCollection<TagItem> ReadFlatObjectTags(JsonElement obj)
    {
        var tags = new List<TagItem>();
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, "tags", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name, "description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in ReadFlatObjectTags(property.Value))
                {
                    tags.Add(new TagItem($"{property.Name}_{nested.Key}", nested.Value));
                }

                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => string.Join(", ", property.Value.EnumerateArray().Select(ReadJsonScalar).Where(static x => !string.IsNullOrWhiteSpace(x))),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                tags.Add(new TagItem(property.Name.Trim(), value.Trim()));
            }
        }

        return tags;
    }

    private static string? ReadJsonScalar(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static IReadOnlyCollection<TagItem> EnrichTags(
        IReadOnlyCollection<TagItem> source,
        string filePath,
        OrderData? orderData)
    {
        var tags = source
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Key) && !string.IsNullOrWhiteSpace(tag.Value))
            .ToList();

        AddIfMissing(tags, "file_name", Path.GetFileName(filePath));
        AddIfMissing(tags, "extension", Path.GetExtension(filePath).TrimStart('.'));

        var description = tags.FirstOrDefault(static tag =>
            string.Equals(tag.Key, "description", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag.Key, "visual_description", StringComparison.OrdinalIgnoreCase))?.Value;
        AddIfMissing(tags, "visual_description", description);

        if (orderData is not null)
        {
            AddIfMissing(tags, "order_id", orderData.OrderId);
            AddIfMissing(tags, "client", orderData.ClientName);
            AddIfMissing(tags, "product_type", orderData.ProductType);
            AddIfMissing(tags, "object_type", orderData.ProductType);
        }

        AddIfMissing(tags, "search_keywords", BuildSearchKeywords(filePath, orderData, description));

        return tags
            .GroupBy(static tag => tag.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static void AddIfMissing(List<TagItem> tags, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            tags.Any(tag => string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        tags.Add(new TagItem(key, value.Trim()));
    }

    private static string BuildSearchKeywords(string filePath, OrderData? orderData, string? description)
    {
        var parts = new List<string>
        {
            Path.GetFileNameWithoutExtension(filePath),
            Path.GetExtension(filePath).TrimStart('.'),
        };

        if (orderData is not null)
        {
            parts.Add(orderData.OrderId);
            parts.Add(orderData.ClientName);
            parts.Add(orderData.ProductType);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description);
        }

        return string.Join(", ", parts.Where(static x => !string.IsNullOrWhiteSpace(x)));
    }

    private static IReadOnlyCollection<TagItem> ReadTagArray(JsonElement array)
    {
        return array.EnumerateArray()
            .Select(static (x, index) =>
            {
                if (x.ValueKind == JsonValueKind.String)
                {
                    var text = x.GetString();
                    return string.IsNullOrWhiteSpace(text)
                        ? null
                        : new TagItem($"search_keyword_{index + 1}", text.Trim());
                }

                if (x.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var key = x.TryGetProperty("key", out var keyProp)
                    ? keyProp.GetString()
                    : x.TryGetProperty("Key", out var altKeyProp)
                        ? altKeyProp.GetString()
                        : null;
                var value = x.TryGetProperty("value", out var valueProp)
                    ? valueProp.GetString()
                    : x.TryGetProperty("Value", out var altValueProp)
                        ? altValueProp.GetString()
                        : null;

                return string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)
                    ? null
                    : new TagItem(key.Trim(), value.Trim());
            })
            .Where(static x => x is not null)
            .Cast<TagItem>()
            .ToList();
    }

    private static IReadOnlyCollection<TagItem> ParseColonTags(string raw)
    {
        return raw
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.Trim().Trim('-', '*', ' ', '\t'))
            .Select(static line =>
            {
                var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
                if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
                {
                    return null;
                }

                var key = line[..separatorIndex].Trim().Trim('«', '»', '"');
                var value = line[(separatorIndex + 1)..].Trim().Trim('«', '»', '"');
                return string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)
                    ? null
                    : new TagItem(key, value);
            })
            .Where(static x => x is not null)
            .Cast<TagItem>()
            .ToList();
    }

    private static string? ExtractJsonArray(string raw)
    {
        var start = raw.IndexOf('[', StringComparison.Ordinal);
        var end = raw.LastIndexOf(']');
        return start >= 0 && end > start
            ? raw[start..(end + 1)]
            : null;
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{', StringComparison.Ordinal);
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start
            ? raw[start..(end + 1)]
            : null;
    }

    private static string GetMimeType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream",
        };
    }
}
