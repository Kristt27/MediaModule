using System.Text.Json;
using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MediaModule.Infrastructure.Persistence;

public sealed class SqliteModuleRepository : IModuleRepository
{
    private readonly ModuleOptions _options;
    private readonly string _connectionString;

    public SqliteModuleRepository(IOptions<ModuleOptions> options)
    {
        _options = options.Value;

        var configuredPath = _options.DatabasePath;
        var dbPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnableWalModeAsync(connection, cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS file_hashes (
    file_path TEXT PRIMARY KEY,
    file_hash TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS file_tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL,
    tag_key TEXT NOT NULL,
    tag_value TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS processing_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_name TEXT NOT NULL,
    file_path TEXT NOT NULL,
    user_name TEXT NOT NULL,
    operation_time_utc TEXT NOT NULL,
    result INTEGER NOT NULL,
    error_ignored INTEGER NOT NULL,
    message TEXT NULL,
    duplicate_of TEXT NULL,
    order_id TEXT NULL,
    tags_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_file_hashes_hash ON file_hashes(file_hash);
CREATE INDEX IF NOT EXISTS idx_log_time ON processing_log(operation_time_utc);
";

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> FindByHashAsync(string hash, string currentFilePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await HasFileHashAsync(connection, currentFilePath, hash, cancellationToken))
        {
            return null;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT file_path, file_hash
FROM file_hashes
WHERE file_path <> $filePath
ORDER BY updated_at_utc DESC;
";
        cmd.Parameters.AddWithValue("$filePath", currentFilePath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidatePath = reader.GetString(0);
            if (PathsEqual(candidatePath, currentFilePath) || !File.Exists(candidatePath))
            {
                continue;
            }

            var candidateHash = reader.GetString(1);
            if (IsDuplicateHash(hash, candidateHash, _options.DuplicateHashDistanceThreshold))
            {
                return candidatePath;
            }
        }

        return null;
    }

    public async Task<bool> HasFileHashAsync(string filePath, string hash, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        return await HasFileHashAsync(connection, filePath, hash, cancellationToken);
    }

    public async Task UpsertFileHashAsync(string filePath, string hash, OrderData? orderData, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO file_hashes(file_path, file_hash, updated_at_utc)
VALUES ($filePath, $hash, $time)
ON CONFLICT(file_path)
DO UPDATE SET file_hash = excluded.file_hash, updated_at_utc = excluded.updated_at_utc;
";
        cmd.Parameters.AddWithValue("$filePath", filePath);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveTagsAsync(string filePath, IReadOnlyCollection<TagItem> tags, OrderData? orderData, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var tag in tags)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO file_tags(file_path, tag_key, tag_value, created_at_utc)
VALUES ($filePath, $key, $value, $time);
";
            cmd.Parameters.AddWithValue("$filePath", filePath);
            cmd.Parameters.AddWithValue("$key", tag.Key);
            cmd.Parameters.AddWithValue("$value", tag.Value);
            cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SaveLogAsync(ProcessingLogEntry entry, OrderData? orderData, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO processing_log(
    file_name,
    file_path,
    user_name,
    operation_time_utc,
    result,
    error_ignored,
    message,
    duplicate_of,
    order_id,
    tags_json)
VALUES (
    $fileName,
    $filePath,
    $userName,
    $time,
    $result,
    $errorIgnored,
    $message,
    $duplicateOf,
    $orderId,
    $tagsJson);
";

        cmd.Parameters.AddWithValue("$fileName", entry.FileName);
        cmd.Parameters.AddWithValue("$filePath", entry.FilePath);
        cmd.Parameters.AddWithValue("$userName", entry.UserName);
        cmd.Parameters.AddWithValue("$time", entry.OperationTimeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$result", (int)entry.Result);
        cmd.Parameters.AddWithValue("$errorIgnored", entry.ErrorIgnored ? 1 : 0);
        cmd.Parameters.AddWithValue("$message", entry.Message ?? string.Empty);
        cmd.Parameters.AddWithValue("$duplicateOf", entry.DuplicateOf ?? string.Empty);
        cmd.Parameters.AddWithValue("$orderId", entry.OrderId ?? string.Empty);
        cmd.Parameters.AddWithValue("$tagsJson", JsonSerializer.Serialize(entry.Tags));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasFileHashAsync(
        SqliteConnection connection,
        string filePath,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT 1
FROM file_hashes
WHERE file_path = $filePath AND file_hash = $hash
LIMIT 1;
";
        cmd.Parameters.AddWithValue("$filePath", filePath);
        cmd.Parameters.AddWithValue("$hash", hash);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task EnableWalModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsDuplicateHash(string currentHash, string candidateHash, int maxDistance)
    {
        if (string.Equals(currentHash, candidateHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsAverageHash(currentHash) || !IsAverageHash(candidateHash) || maxDistance <= 0)
        {
            return false;
        }

        return GetHexHammingDistance(currentHash, candidateHash) <= Math.Clamp(maxDistance, 0, 64);
    }

    private static bool IsAverageHash(string hash)
    {
        return hash.Length == 16 && hash.All(Uri.IsHexDigit);
    }

    private static int GetHexHammingDistance(string left, string right)
    {
        var distance = 0;
        for (var index = 0; index < left.Length; index++)
        {
            var diff = GetHexValue(left[index]) ^ GetHexValue(right[index]);
            while (diff != 0)
            {
                distance += diff & 1;
                diff >>= 1;
            }
        }

        return distance;
    }

    private static int GetHexValue(char value)
    {
        return value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => 0,
        };
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            GetFullPathOrOriginal(left),
            GetFullPathOrOriginal(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFullPathOrOriginal(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
