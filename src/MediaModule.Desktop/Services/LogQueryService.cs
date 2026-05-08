using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaModule.Desktop.Models;
using Microsoft.Data.Sqlite;

namespace MediaModule.Desktop.Services;

public sealed class LogQueryService
{
    private static readonly Regex LegacyKeyValueRegex = new(
        "\"key\"\\s*:\\s*\"(?<key>(?:\\\\.|[^\"])*)\"\\s*,\\s*\"value\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<IReadOnlyCollection<ProcessingLogRow>> GetRecentAsync(string databasePath, string filter, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return Array.Empty<ProcessingLogRow>();
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        var readRows = new List<ProcessingLogRow>();
        var rows = new List<ProcessingLogRow>();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, operation_time_utc, file_name, file_path, result, error_ignored, message, order_id, duplicate_of, tags_json
FROM processing_log
ORDER BY id DESC
LIMIT $limit;
";

        cmd.Parameters.AddWithValue("$limit", Math.Max(limit, 300));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ProcessingLogRow
            {
                Id = reader.GetInt32(0),
                OperationTimeUtc = reader.GetString(1),
                FileName = reader.GetString(2),
                FilePath = reader.GetString(3),
                Result = MapResult(reader.GetInt32(4)),
                ErrorIgnored = reader.GetInt32(5) == 1,
                Message = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                OrderId = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                DuplicateOf = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                TagsJson = reader.IsDBNull(9) ? "[]" : reader.GetString(9),
            };

            readRows.Add(row);
        }

        var storedTagsByPath = await LoadStoredTagsAsync(
            connection,
            readRows.Select(static row => row.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);

        foreach (var row in readRows)
        {
            var normalizedTags = ParseTags(row.TagsJson).ToList();
            if (storedTagsByPath.TryGetValue(row.FilePath, out var storedTags))
            {
                normalizedTags.AddRange(storedTags);
            }

            row.NormalizedTags = NormalizeTags(normalizedTags).ToList();
            if (MatchesFilter(row, filter))
            {
                rows.Add(row);
            }
        }

        return rows.Take(limit).ToList();
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyCollection<TagRow>>> LoadStoredTagsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
        {
            return new Dictionary<string, IReadOnlyCollection<TagRow>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, List<TagRow>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd = connection.CreateCommand();
            var parameterNames = filePaths.Select(static (_, index) => $"$path{index}").ToList();
            cmd.CommandText = $@"
SELECT file_path, tag_key, tag_value
FROM file_tags
WHERE file_path IN ({string.Join(", ", parameterNames)})
ORDER BY id;
";

            for (var index = 0; index < filePaths.Count; index++)
            {
                cmd.Parameters.AddWithValue(parameterNames[index], filePaths[index]);
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var path = reader.GetString(0);
                if (!result.TryGetValue(path, out var tags))
                {
                    tags = new List<TagRow>();
                    result[path] = tags;
                }

                tags.Add(new TagRow
                {
                    Key = reader.GetString(1),
                    Value = reader.GetString(2),
                });
            }
        }
        catch (SqliteException)
        {
            return new Dictionary<string, IReadOnlyCollection<TagRow>>(StringComparer.OrdinalIgnoreCase);
        }

        return result.ToDictionary(
            static item => item.Key,
            static item => (IReadOnlyCollection<TagRow>)NormalizeTags(item.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<TagRow> ParseTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<TagRow>();
        }

        try
        {
            var doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<TagRow>();
            }

            var parsed = doc.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .SelectMany(ReadTagObject)
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .ToList();

            return NormalizeTags(parsed);
        }
        catch
        {
            return TryRepairMalformedJsonTag(tagsJson);
        }
    }

    private static IReadOnlyCollection<TagRow> ReadTagObject(JsonElement x)
    {
        var key = x.TryGetProperty("Key", out var keyProp)
            ? keyProp.GetString()
            : x.TryGetProperty("key", out var altKeyProp)
                ? altKeyProp.GetString()
                : null;
        var value = x.TryGetProperty("Value", out var valueProp)
            ? valueProp.GetString()
            : x.TryGetProperty("value", out var altValueProp)
                ? altValueProp.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<TagRow>();
        }

        var repaired = TryRepairJsonTag(key, value);
        return repaired.Count > 0
            ? repaired
            : new[] { new TagRow { Key = key.Trim(), Value = value.Trim() } };
    }

    private static IReadOnlyCollection<TagRow> TryRepairJsonTag(string key, string value)
    {
        var raw = $"{key}:{value}".Trim();
        if (!raw.StartsWith('{') || !raw.Contains("\"tags\"", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<TagRow>();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ReadJsonObjectTags(doc.RootElement);
        }
        catch
        {
            return TryRepairMalformedJsonTag(raw);
        }
    }

    private static IReadOnlyCollection<TagRow> TryRepairMalformedJsonTag(string raw)
    {
        var result = new List<TagRow>();
        var normalizedRaw = raw.Replace("\"\"", "\"", StringComparison.Ordinal);
        var tagsIndex = normalizedRaw.IndexOf("\"tags\"", StringComparison.OrdinalIgnoreCase);
        var descriptionStart = normalizedRaw.IndexOf(':');
        if (descriptionStart > 0 && tagsIndex > descriptionStart)
        {
            var description = normalizedRaw[(descriptionStart + 1)..tagsIndex]
                .Trim()
                .Trim(',', '"', ' ');
            if (!string.IsNullOrWhiteSpace(description))
            {
                result.Add(new TagRow { Key = "description", Value = description });
            }
        }

        result.AddRange(ReadLegacyKeyValueTags(normalizedRaw));

        var tagsStart = normalizedRaw.IndexOf('[', tagsIndex >= 0 ? tagsIndex : 0);
        var tagsEnd = normalizedRaw.LastIndexOf(']');
        if (tagsStart < 0 || tagsEnd <= tagsStart)
        {
            return NormalizeTags(result);
        }

        try
        {
            var tagsJson = normalizedRaw[tagsStart..(tagsEnd + 1)];
            using var tagsDoc = JsonDocument.Parse(tagsJson);
            if (tagsDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(ReadTagArray(tagsDoc.RootElement));
            }
        }
        catch
        {
        }

        return NormalizeTags(result);
    }

    private static IReadOnlyCollection<TagRow> ReadLegacyKeyValueTags(string raw)
    {
        return LegacyKeyValueRegex.Matches(raw)
            .Select(static match => new TagRow
            {
                Key = CleanLegacyJsonText(match.Groups["key"].Value),
                Value = CleanLegacyJsonText(match.Groups["value"].Value),
            })
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Key) && !string.IsNullOrWhiteSpace(tag.Value))
            .ToList();
    }

    private static string CleanLegacyJsonText(string value)
    {
        return value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Trim();
    }

    private static IReadOnlyCollection<TagRow> ReadJsonObjectTags(JsonElement obj)
    {
        var tags = new List<TagRow>();
        if (obj.TryGetProperty("description", out var descriptionProp))
        {
            var description = descriptionProp.GetString();
            if (!string.IsNullOrWhiteSpace(description))
            {
                tags.Add(new TagRow { Key = "description", Value = description.Trim() });
            }
        }

        if (obj.TryGetProperty("tags", out var tagsProp))
        {
            if (tagsProp.ValueKind == JsonValueKind.Array)
            {
                tags.AddRange(ReadTagArray(tagsProp));
            }
            else if (tagsProp.ValueKind == JsonValueKind.Object)
            {
                tags.AddRange(ReadFlatObjectTags(tagsProp));
            }
        }

        tags.AddRange(ReadFlatObjectTags(obj));
        return NormalizeTags(tags);
    }

    private static IReadOnlyCollection<TagRow> ReadTagArray(JsonElement array)
    {
        return array.EnumerateArray()
            .SelectMany(static (item, index) =>
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    return string.IsNullOrWhiteSpace(text)
                        ? Array.Empty<TagRow>()
                        : new[] { new TagRow { Key = $"search_keyword_{index + 1}", Value = text.Trim() } };
                }

                return item.ValueKind == JsonValueKind.Object
                    ? ReadTagObject(item)
                    : Array.Empty<TagRow>();
            })
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
                JsonValueKind.Array => string.Join(", ", property.Value.EnumerateArray().Select(ReadScalar).Where(static x => !string.IsNullOrWhiteSpace(x))),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                tags.Add(new TagRow { Key = property.Name.Trim(), Value = value.Trim() });
            }
        }

        return tags;
    }

    private static string? ReadScalar(JsonElement element)
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

    private static IReadOnlyCollection<TagRow> NormalizeTags(IReadOnlyCollection<TagRow> tags)
    {
        return tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Key) && !string.IsNullOrWhiteSpace(tag.Value))
            .GroupBy(static tag => tag.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => new TagRow
            {
                Key = group.First().Key.Trim(),
                Value = string.Join(", ", group.Select(static tag => tag.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
            })
            .ToList();
    }

    private static string MapResult(int result)
    {
        return result switch
        {
            0 => "Успешно",
            1 => "Заблокировано",
            2 => "Сохранено с нарушением",
            3 => "Дубликат найден",
            4 => "Ошибка",
            5 => "Пропущено",
            6 => "Исправлено пользователем",
            _ => "Неизвестно",
        };
    }

    public bool MatchesFilter(ProcessingLogRow row, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var haystack = string.Join(
            " ",
            row.FileName,
            row.FilePath,
            row.Result,
            row.Message,
            row.OrderId,
            row.TagsJson,
            string.Join(" ", row.NormalizedTags.Select(static tag => $"{tag.Key} {tag.Value}"))).ToLowerInvariant();

        var tokenGroups = ExpandSearchTokenGroups(filter);
        return tokenGroups.All(group => group.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyCollection<IReadOnlyCollection<string>> ExpandSearchTokenGroups(string filter)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["синий"] = new[] { "синий", "blue" },
            ["синяя"] = new[] { "синяя", "blue" },
            ["баннер"] = new[] { "баннер", "banner" },
            ["логотип"] = new[] { "логотип", "logo" },
            ["постер"] = new[] { "постер", "poster" },
            ["плакат"] = new[] { "плакат", "poster" },
            ["минималистичный"] = new[] { "минималистичный", "minimalism", "minimal" },
            ["минимализм"] = new[] { "минимализм", "minimalism", "minimal" },
            ["белый"] = new[] { "белый", "white" },
            ["красный"] = new[] { "красный", "red" },
            ["черный"] = new[] { "черный", "black" },
        };

        return filter
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token =>
            {
                var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (map.TryGetValue(token, out var replacements))
                {
                    foreach (var replacement in replacements)
                    {
                        variants.Add(replacement);
                    }
                }
                else
                {
                    variants.Add(token);
                }

                foreach (var variant in variants.ToList())
                {
                    variants.Add(TransliterateRussianToLatin(variant));
                }

                return (IReadOnlyCollection<string>)variants
                    .Where(static variant => !string.IsNullOrWhiteSpace(variant))
                    .Select(static variant => variant.ToLowerInvariant())
                    .ToList();
            })
            .ToList();
    }

    private static string TransliterateRussianToLatin(string value)
    {
        var result = new List<string>(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            result.Add(character switch
            {
                'а' => "a",
                'б' => "b",
                'в' => "v",
                'г' => "g",
                'д' => "d",
                'е' or 'ё' => "e",
                'ж' => "zh",
                'з' => "z",
                'и' => "i",
                'й' => "y",
                'к' => "k",
                'л' => "l",
                'м' => "m",
                'н' => "n",
                'о' => "o",
                'п' => "p",
                'р' => "r",
                'с' => "s",
                'т' => "t",
                'у' => "u",
                'ф' => "f",
                'х' => "kh",
                'ц' => "ts",
                'ч' => "ch",
                'ш' => "sh",
                'щ' => "shch",
                'ы' => "y",
                'э' => "e",
                'ю' => "yu",
                'я' => "ya",
                'ъ' or 'ь' => string.Empty,
                _ => character.ToString(),
            });
        }

        return string.Concat(result);
    }
}
