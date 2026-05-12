using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Domain.Entities;
using MediaModule.Infrastructure.PathResolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaModule.Infrastructure.Integration;

public sealed class RealElmaClient : IElmaClient
{
    private readonly ElmaOptions _options;
    private readonly ModuleOptions _moduleOptions;
    private readonly ILogger<RealElmaClient> _logger;
    private readonly HttpClient _httpClient;

    public RealElmaClient(IOptions<ModuleOptions> options, ILogger<RealElmaClient> logger)
    {
        _moduleOptions = options.Value;
        _options = _moduleOptions.Elma;
        ApplyEnvironmentOverrides(_options);
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)),
        };
    }

    public async Task<OrderData?> TryResolveOrderAsync(string filePath, CancellationToken cancellationToken)
    {
        var orders = await GetOrdersAsync(cancellationToken);
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var chunks = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (chunks.Length >= 2)
        {
            var byName = orders.FirstOrDefault(
                x => string.Equals(x.ClientName, chunks[0], StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.ProductType, chunks[1], StringComparison.OrdinalIgnoreCase));

            if (byName is not null)
            {
                return byName;
            }
        }

        var root = ModulePathResolver.Resolve(_moduleOptions.RootDirectory);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var relative = Path.GetRelativePath(root, directory);
            var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var segments = relative.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length >= 2)
            {
                return orders.FirstOrDefault(
                    x => string.Equals(x.ClientName, segments[0], StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.ProductType, segments[1], StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    public async Task<IReadOnlyCollection<OrderData>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured())
        {
            return Array.Empty<OrderData>();
        }

        try
        {
            using var request = new HttpRequestMessage(GetRequestMethod(), BuildApiUrl());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
            if (request.Method != HttpMethod.Get)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        active = true,
                        size = Math.Clamp(_options.PageSize, 1, 10000),
                        from = 0,
                    }),
                    Encoding.UTF8,
                    "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var orders = ReadOrders(doc.RootElement);
            return orders;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ELMA API недоступна, список заказов не загружен. Пользователь может ввести данные вручную.");
            return Array.Empty<OrderData>();
        }
    }

    private bool IsConfigured()
    {
        return _options.Enabled
            && !string.IsNullOrWhiteSpace(_options.Token)
            && !string.IsNullOrWhiteSpace(_options.BaseUrl)
            && !string.IsNullOrWhiteSpace(_options.Namespace)
            && !string.IsNullOrWhiteSpace(_options.AppCode);
    }

    private string BuildApiUrl()
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var ns = Uri.EscapeDataString(_options.Namespace.Trim());
        var appCode = Uri.EscapeDataString(_options.AppCode.Trim());
        return $"{baseUrl}/pub/v1/app/{ns}/{appCode}/list";
    }

    private HttpMethod GetRequestMethod()
    {
        return string.Equals(_options.RequestMethod, "GET", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Get
            : HttpMethod.Post;
    }

    private IReadOnlyCollection<OrderData> ReadOrders(JsonElement root)
    {
        var items = FindItemsArray(root);
        if (items is null)
        {
            return Array.Empty<OrderData>();
        }

        var orders = new List<OrderData>();
        foreach (var item in items.Value.EnumerateArray())
        {
            var orderId = ReadField(item, _options.OrderIdField);
            var clientName = ReadField(item, _options.ClientNameField);
            var productType = ReadField(item, _options.ProductTypeField);

            if (string.IsNullOrWhiteSpace(orderId) ||
                string.IsNullOrWhiteSpace(clientName) ||
                string.IsNullOrWhiteSpace(productType))
            {
                continue;
            }

            orders.Add(new OrderData(orderId, clientName, productType)
            {
                Status = ReadField(item, _options.StatusField) ?? "Completed",
                CompletedAtUtc = ReadDateField(item, _options.CompletedAtField),
            });
        }

        return orders;
    }

    private static JsonElement? FindItemsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "result", "items", "data" })
        {
            if (!root.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Array)
            {
                return property;
            }

            if (property.ValueKind == JsonValueKind.Object)
            {
                var nested = FindItemsArray(property);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? ReadField(JsonElement item, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        foreach (var source in EnumerateFieldSources(item))
        {
            if (TryGetPropertyCaseInsensitive(source, fieldName, out var property))
            {
                return ReadScalar(property);
            }
        }

        return null;
    }

    private static DateTime? ReadDateField(JsonElement item, string fieldName)
    {
        var raw = ReadField(item, fieldName);
        return DateTime.TryParse(raw, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static IEnumerable<JsonElement> EnumerateFieldSources(JsonElement item)
    {
        yield return item;

        foreach (var name in new[] { "context", "fields", "data" })
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty(name, out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                yield return nested;
            }
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement source, string name, out JsonElement property)
    {
        foreach (var candidate in source.EnumerateObject())
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? ReadScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object => ReadObjectScalar(value),
            _ => null,
        };
    }

    private static string? ReadObjectScalar(JsonElement value)
    {
        foreach (var name in new[] { "name", "displayName", "value", "code", "id" })
        {
            if (value.TryGetProperty(name, out var property))
            {
                var scalar = ReadScalar(property);
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    return scalar;
                }
            }
        }

        return null;
    }

    private static void ApplyEnvironmentOverrides(ElmaOptions options)
    {
        ApplyEnvironmentValue("ELMA_API_TOKEN", value =>
        {
            options.Token = value;
            options.Enabled = true;
        });
        ApplyEnvironmentValue("ELMA_BASE_URL", value => options.BaseUrl = value);
        ApplyEnvironmentValue("ELMA_NAMESPACE", value => options.Namespace = value);
        ApplyEnvironmentValue("ELMA_APP_CODE", value => options.AppCode = value);
    }

    private static void ApplyEnvironmentValue(string name, Action<string> apply)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value.Trim());
        }
    }
}
