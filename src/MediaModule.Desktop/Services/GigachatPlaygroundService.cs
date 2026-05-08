using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaModule.Desktop.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaModule.Desktop.Services;

public sealed class GigachatPlaygroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyCollection<TagRow>> GenerateRealTagsAsync(
        string filePath,
        string orderId,
        WorkerSettingsSnapshot settings,
        CancellationToken cancellationToken)
    {
        if (!settings.GigaChatEnabled)
        {
            throw new InvalidOperationException("GigaChat выключен в appsettings.json: Module:GigaChat:Enabled=false.");
        }

        var authorizationKey = GetAuthorizationKey(settings);
        if (string.IsNullOrWhiteSpace(authorizationKey))
        {
            throw new InvalidOperationException("Не указан AuthorizationKey для GigaChat.");
        }

        using var httpClient = new HttpClient(CreateHttpClientHandler(settings))
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        var token = await GetAccessTokenAsync(httpClient, settings, authorizationKey, cancellationToken);
        var attachmentId = await UploadFileAsync(httpClient, filePath, settings, token, cancellationToken);
        var order = FindOrder(orderId, settings.OrdersMultiline);
        var prompt = BuildPrompt(filePath, order);
        var payload = CreateChatPayload(settings, prompt, attachmentId);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl(settings, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent(payload);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        var tags = ParseTags(content);
        if (tags.Count == 0)
        {
            throw new InvalidOperationException("GigaChat вернул ответ без распознаваемых тегов.");
        }

        return tags;
    }

    /// <summary>
    /// Формирует демонстрационный набор тегов по имени файла и номеру заказа
    /// для сценария тестирования тегирования в интерфейсе.
    /// </summary>
    public IReadOnlyCollection<TagRow> GenerateMockTags(string fileName, string orderId)
    {
        var tags = new List<TagRow>();

        var normalized = fileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return tags;
        }

        var extension = Path.GetExtension(normalized).Trim('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(extension))
        {
            tags.Add(new TagRow { Key = "extension", Value = extension });
        }

        var pureName = Path.GetFileNameWithoutExtension(normalized).ToLowerInvariant();
        var objectType = InferObjectType(pureName);
        var imageSummary = ReadImageSummary(normalized);
        var colors = imageSummary?.DominantColors.Count > 0
            ? string.Join(", ", imageSummary.DominantColors)
            : "цвета определит GigaChat";
        var format = imageSummary is null
            ? "формат определит GigaChat"
            : $"{imageSummary.Orientation}, {imageSummary.Width}x{imageSummary.Height}";
        var description = imageSummary is null
            ? $"Графический макет типа {objectType}; точное описание объектов, текста и цветов сформирует GigaChat по изображению."
            : $"Графический макет типа {objectType}, {imageSummary.Orientation}; основные цвета: {colors}.";

        tags.Add(new TagRow { Key = "visual_description", Value = description });
        tags.Add(new TagRow { Key = "visible_text", Value = "читаемые надписи определит GigaChat по изображению" });
        tags.Add(new TagRow { Key = "dominant_colors", Value = colors });
        tags.Add(new TagRow { Key = "background", Value = imageSummary?.Background ?? "фон определит GigaChat" });
        tags.Add(new TagRow { Key = "composition", Value = InferComposition(objectType) });
        tags.Add(new TagRow { Key = "object_type", Value = objectType });
        tags.Add(new TagRow { Key = "product_type", Value = objectType });
        tags.Add(new TagRow { Key = "purpose", Value = InferPurpose(objectType) });

        tags.Add(new TagRow
        {
            Key = "style",
            Value = pureName.Contains("minimal") || pureName.Contains("минимал")
                ? "minimalism"
                : "визуальный макет",
        });
        tags.Add(new TagRow { Key = "mood", Value = InferMood(objectType) });
        tags.Add(new TagRow { Key = "audience", Value = InferAudience(objectType) });
        tags.Add(new TagRow { Key = "format", Value = format });
        tags.Add(new TagRow { Key = "search_keywords", Value = $"{objectType}, макет, дизайн, {colors}, {format}, визуальный поиск" });

        if (!string.IsNullOrWhiteSpace(orderId))
        {
            tags.Add(new TagRow { Key = "order_id", Value = orderId.Trim() });
        }

        return tags;
    }

    public string BuildRequestPreview(string fileName, string orderId, WorkerSettingsSnapshot settings)
    {
        var order = FindOrder(orderId, settings.OrdersMultiline);
        var imagePath = string.IsNullOrWhiteSpace(fileName)
            ? "ivanov_banner_2026_1.png"
            : fileName.Trim();
        var filePath = BuildPreviewFilePath(imagePath, settings.RootDirectory, order);
        var apiBaseUrl = string.IsNullOrWhiteSpace(settings.GigaChatApiBaseUrl)
            ? "https://gigachat.devices.sberbank.ru/api/v1"
            : settings.GigaChatApiBaseUrl.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(settings.GigaChatModel)
            ? "GigaChat-Pro"
            : settings.GigaChatModel.Trim();
        var prompt = BuildPrompt(filePath, order);
        var attachmentId = "file-id-from-upload";

        var uploadPayload = BuildImagePreview(filePath);
        var chatPayload = CreateChatPayload(model, prompt, attachmentId);

        return string.Join(
            Environment.NewLine,
            $"UPLOAD IMAGE -> POST {apiBaseUrl}/files",
            "Authorization: Bearer <access_token из OAuth>",
            "Content-Type: multipart/form-data",
            JsonSerializer.Serialize(uploadPayload, JsonOptions),
            string.Empty,
            $"POST {apiBaseUrl}/chat/completions",
            "Authorization: Bearer <access_token из OAuth>",
            "Content-Type: application/json; charset=utf-8",
            string.Empty,
            "PROMPT -> messages[0].content",
            prompt,
            string.Empty,
            "CHAT JSON BODY",
            JsonSerializer.Serialize(chatPayload, JsonOptions));
    }

    private static string BuildPrompt(string filePath, PlaygroundOrder? order)
    {
        return string.Join(
            Environment.NewLine,
            "Проанализируй прикрепленный графический файл и связанные с ним данные.",
            $"Имя файла: {Path.GetFileName(filePath)}",
            $"Расширение: {Path.GetExtension(filePath)}",
            $"OrderId: {order?.OrderId ?? "не указан"}",
            $"Клиент: {order?.ClientName ?? "не указан"}",
            $"Тип продукта: {order?.ProductType ?? "не указан"}",
            "Сначала опиши изображение как человек, который ищет макет глазами: что изображено, какие объекты и персонажи есть, какие видимые надписи, основные цвета, фон, композиция, стиль и назначение.",
            "После описания сформируй поисковые характеристики. Они должны помогать найти макет по визуальной памяти: «фиолетовая памятка с таблицей», «баннер с синей кнопкой», «постер с крупным заголовком».",
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
            "Не используй значения unknown, generic, «неизвестно» и пустые значения. Если признак нельзя уверенно определить визуально, напиши полезное приближение вроде «информационный макет» или «светлый фон».",
            "Верни строго JSON без markdown и пояснений в формате:",
            "{\"description\":\"общее описание\",\"tags\":[{\"key\":\"visual_description\",\"value\":\"...\"},{\"key\":\"visible_text\",\"value\":\"...\"},{\"key\":\"dominant_colors\",\"value\":\"...\"},{\"key\":\"background\",\"value\":\"...\"},{\"key\":\"composition\",\"value\":\"...\"},{\"key\":\"object_type\",\"value\":\"...\"},{\"key\":\"product_type\",\"value\":\"...\"},{\"key\":\"style\",\"value\":\"...\"},{\"key\":\"mood\",\"value\":\"...\"},{\"key\":\"purpose\",\"value\":\"...\"},{\"key\":\"audience\",\"value\":\"...\"},{\"key\":\"format\",\"value\":\"...\"},{\"key\":\"client\",\"value\":\"...\"},{\"key\":\"order_id\",\"value\":\"...\"},{\"key\":\"search_keywords\",\"value\":\"...\"}]}");
    }

    private static VisualImageSummary? ReadImageSummary(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            var colorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var step = Math.Max(1, Math.Min(width, height) / 80);
            var brightnessTotal = 0d;
            var samples = 0;

            for (var y = 0; y < height; y += step)
            {
                for (var x = 0; x < width; x += step)
                {
                    var index = y * stride + x * 4;
                    var b = pixels[index];
                    var g = pixels[index + 1];
                    var r = pixels[index + 2];
                    var a = pixels[index + 3];
                    if (a < 32)
                    {
                        continue;
                    }

                    var colorName = GetColorName(r, g, b);
                    colorCounts[colorName] = colorCounts.TryGetValue(colorName, out var count) ? count + 1 : 1;
                    brightnessTotal += (r + g + b) / 3d;
                    samples++;
                }
            }

            var dominantColors = colorCounts
                .OrderByDescending(static pair => pair.Value)
                .Take(5)
                .Select(static pair => pair.Key)
                .ToList();
            var orientation = width > height * 1.15
                ? "горизонтальный"
                : height > width * 1.15
                    ? "вертикальный"
                    : "квадратный";
            var background = samples == 0 || brightnessTotal / samples > 180
                ? "светлый фон"
                : "темный или насыщенный фон";

            return new VisualImageSummary(width, height, orientation, dominantColors, background);
        }
        catch
        {
            return null;
        }
    }

    private static string GetColorName(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));

        if (max < 45)
        {
            return "черный";
        }

        if (min > 220)
        {
            return "белый";
        }

        if (max - min < 25)
        {
            return max > 170 ? "светло-серый" : "серый";
        }

        if (r > 180 && g > 150 && b < 90)
        {
            return "желтый";
        }

        if (r > 170 && g < 110 && b < 110)
        {
            return "красный";
        }

        if (r > 180 && g > 90 && g < 170 && b < 90)
        {
            return "оранжевый";
        }

        if (r > 130 && b > 150 && g < 140)
        {
            return "фиолетовый";
        }

        if (b > 150 && r < 130)
        {
            return "синий";
        }

        if (g > 140 && r < 150 && b < 140)
        {
            return "зеленый";
        }

        if (r > 150 && g > 120 && b > 120)
        {
            return "розовый";
        }

        return "смешанные цвета";
    }

    private static string InferObjectType(string pureName)
    {
        if (pureName.Contains("banner") || pureName.Contains("баннер"))
        {
            return "banner";
        }

        if (pureName.Contains("poster") || pureName.Contains("постер") || pureName.Contains("афиша"))
        {
            return "poster";
        }

        if (pureName.Contains("logo") || pureName.Contains("логотип"))
        {
            return "logo";
        }

        if (pureName.Contains("памятка") || pureName.Contains("инструкция"))
        {
            return "instruction";
        }

        return "design layout";
    }

    private static string InferPurpose(string objectType)
    {
        return objectType switch
        {
            "banner" => "advertising",
            "poster" => "announcement",
            "logo" => "branding",
            "instruction" => "information",
            _ => "design search",
        };
    }

    private static string InferComposition(string objectType)
    {
        return objectType switch
        {
            "banner" => "широкая рекламная композиция",
            "poster" => "вертикальная афишная композиция с акцентом на заголовок",
            "logo" => "центральный знак или брендовый элемент",
            "instruction" => "информационная структура с блоками, текстом или таблицей",
            _ => "композицию точнее определит GigaChat",
        };
    }

    private static string InferMood(string objectType)
    {
        return objectType switch
        {
            "instruction" => "учебный, информационный",
            "logo" => "брендовый",
            "poster" => "анонсирующий",
            "banner" => "рекламный",
            _ => "визуальный",
        };
    }

    private static string InferAudience(string objectType)
    {
        return objectType switch
        {
            "instruction" => "читатели инструкции или памятки",
            "logo" => "клиенты бренда",
            "poster" => "посетители мероприятия",
            "banner" => "потенциальные покупатели",
            _ => "пользователи макета",
        };
    }

    private static string BuildPreviewFilePath(string fileName, string rootDirectory, PlaygroundOrder? order)
    {
        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return fileName;
        }

        return order is null
            ? Path.Combine(rootDirectory, fileName)
            : Path.Combine(rootDirectory, order.ClientName, order.ProductType, fileName);
    }

    private static object BuildImagePreview(string filePath)
    {
        var fileInfo = File.Exists(filePath) ? new FileInfo(filePath) : null;

        return new
        {
            file_name = Path.GetFileName(filePath),
            mime_type = GetMimeType(filePath),
            size_bytes = fileInfo?.Length,
            purpose = "general",
            file = "<binary image content hidden in preview>",
        };
    }

    private static async Task<string> GetAccessTokenAsync(
        HttpClient httpClient,
        WorkerSettingsSnapshot settings,
        string authorizationKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.GigaChatOAuthUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorizationKey);
        request.Headers.Add("RqUID", Guid.NewGuid().ToString());
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["scope"] = string.IsNullOrWhiteSpace(settings.GigaChatScope)
                ? "GIGACHAT_API_PERS"
                : settings.GigaChatScope.Trim(),
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
    }

    private static HttpClientHandler CreateHttpClientHandler(WorkerSettingsSnapshot settings)
    {
        var handler = new HttpClientHandler();
        if (settings.GigaChatIgnoreSslCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private static async Task<string> UploadFileAsync(
        HttpClient httpClient,
        string filePath,
        WorkerSettingsSnapshot settings,
        string token,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(filePath));

        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        form.Add(new StringContent("general"), "purpose");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl(settings, "files"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = form;

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var attachmentId = doc.RootElement.TryGetProperty("id", out var id)
            ? id.GetString()
            : doc.RootElement.TryGetProperty("id_", out var altId)
                ? altId.GetString()
                : null;

        return string.IsNullOrWhiteSpace(attachmentId)
            ? throw new InvalidOperationException("GigaChat не вернул id загруженного файла.")
            : attachmentId;
    }

    private static object CreateChatPayload(WorkerSettingsSnapshot settings, string prompt, string attachmentId)
    {
        var model = string.IsNullOrWhiteSpace(settings.GigaChatModel)
            ? "GigaChat-Pro"
            : settings.GigaChatModel.Trim();

        return CreateChatPayload(model, prompt, attachmentId);
    }

    private static object CreateChatPayload(string model, string prompt, string attachmentId)
    {
        return new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt,
                    attachments = new[] { attachmentId },
                },
            },
            temperature = 0.2,
        };
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string BuildApiUrl(WorkerSettingsSnapshot settings, string relativePath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(settings.GigaChatApiBaseUrl)
            ? "https://gigachat.devices.sberbank.ru/api/v1"
            : settings.GigaChatApiBaseUrl.TrimEnd('/');

        return $"{baseUrl}/{relativePath.TrimStart('/')}";
    }

    private static string GetAuthorizationKey(WorkerSettingsSnapshot settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GigaChatAuthorizationKey))
        {
            return settings.GigaChatAuthorizationKey.Trim();
        }

        return Environment.GetEnvironmentVariable("GIGACHAT_AUTHORIZATION_KEY")?.Trim() ?? string.Empty;
    }

    private static IReadOnlyCollection<TagRow> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<TagRow>();
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
            return Array.Empty<TagRow>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? ReadTagArray(doc.RootElement)
                : Array.Empty<TagRow>();
        }
        catch
        {
            return Array.Empty<TagRow>();
        }
    }

    private static IReadOnlyCollection<TagRow> ParseJsonObjectTags(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<TagRow>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<TagRow>();
            }

            var tags = new List<TagRow>();
            if (doc.RootElement.TryGetProperty("description", out var descriptionProp))
            {
                var description = descriptionProp.GetString();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    tags.Add(new TagRow { Key = "description", Value = description.Trim() });
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
            return Array.Empty<TagRow>();
        }
    }

    private static IReadOnlyCollection<TagRow> ReadTagArray(JsonElement array)
    {
        return array.EnumerateArray()
            .Select(static (x, index) =>
            {
                if (x.ValueKind == JsonValueKind.String)
                {
                    var text = x.GetString();
                    return string.IsNullOrWhiteSpace(text)
                        ? null
                        : new TagRow { Key = $"search_keyword_{index + 1}", Value = text.Trim() };
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
                    : new TagRow { Key = key.Trim(), Value = value.Trim() };
            })
            .Where(static x => x is not null)
            .Cast<TagRow>()
            .ToList();
    }

    private static IReadOnlyCollection<TagRow> ReadFlatObjectTags(JsonElement obj)
    {
        var tags = new List<TagRow>();
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
                    tags.Add(new TagRow { Key = $"{property.Name}_{nested.Key}", Value = nested.Value });
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
                tags.Add(new TagRow { Key = property.Name.Trim(), Value = value.Trim() });
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

    private static IReadOnlyCollection<TagRow> ParseColonTags(string raw)
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
                    : new TagRow { Key = key, Value = value };
            })
            .Where(static x => x is not null)
            .Cast<TagRow>()
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

    private static PlaygroundOrder? FindOrder(string orderId, string ordersMultiline)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return null;
        }

        foreach (var line in ordersMultiline.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !string.Equals(parts[0], orderId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new PlaygroundOrder(parts[0], parts[1], parts[2]);
        }

        return null;
    }

    private sealed record PlaygroundOrder(string OrderId, string ClientName, string ProductType);

    private sealed record VisualImageSummary(
        int Width,
        int Height,
        string Orientation,
        IReadOnlyCollection<string> DominantColors,
        string Background);
}
