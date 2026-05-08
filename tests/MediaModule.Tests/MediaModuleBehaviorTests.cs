using MediaModule.Application.Abstractions;
using MediaModule.Application.Configuration;
using MediaModule.Application.Services;
using MediaModule.Desktop.Services;
using MediaModule.Domain.Entities;
using MediaModule.Infrastructure.Persistence;
using MediaModule.Infrastructure.Services;
using MediaModule.Infrastructure.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaModule.Tests;

public sealed class MediaModuleBehaviorTests
{
    [Fact]
    public void Validator_rejects_wrong_name_and_wrong_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = new TestOptionsMonitor<ModuleOptions>(new ModuleOptions
        {
            RootDirectory = root,
            ValidateFileName = true,
            ValidatePath = true,
            FileNameRegexPattern = "^[A-Za-z]+_[A-Za-z]+_20\\d{2}_\\d+\\.[A-Za-z0-9]+$",
        });
        var validator = new RegexFileRuleValidator(options);
        var order = new OrderData("1001", "Ivanov", "banner");

        var result = validator.Validate(Path.Combine(root, "Wrong", "abracadabra.png"), order);

        Assert.True(result.HasViolations);
        Assert.False(result.IsNameValid);
        Assert.False(result.IsPathValid);
        Assert.Contains(Path.Combine(root, "Ivanov", "banner"), result.RecommendedDirectory);
        Assert.Equal("Ivanov_banner_2026_1.png", result.RecommendedFileName);
    }

    [Fact]
    public void Violation_policy_blocks_first_attempt_and_ignores_second_attempt()
    {
        var policy = new InMemoryViolationPolicy();
        var path = @"C:\Temp\abracadabra.png";

        var first = policy.RegisterViolation(path);
        var second = policy.RegisterViolation(path);

        Assert.True(first.ShouldBlock);
        Assert.False(first.IsIgnored);
        Assert.False(second.ShouldBlock);
        Assert.True(second.IsIgnored);
    }

    [Fact]
    public async Task Repository_finds_near_duplicate_hash_and_ignores_deleted_candidates()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var dbPath = Path.Combine(root, "module.db");
            var repository = new SqliteModuleRepository(Options.Create(new ModuleOptions
            {
                DatabasePath = dbPath,
                DuplicateHashDistanceThreshold = 5,
            }));

            await repository.InitializeAsync(CancellationToken.None);

            var deletedPath = Path.Combine(root, "deleted.png");
            await repository.UpsertFileHashAsync(deletedPath, "0000000000000000", null, CancellationToken.None);

            var firstResult = await repository.FindByHashAsync(
                "0000000000000003",
                Path.Combine(root, "candidate.png"),
                CancellationToken.None);

            Assert.Null(firstResult);

            var originalPath = Path.Combine(root, "Ivanov_banner_2026_1.png");
            await File.WriteAllTextAsync(originalPath, "image", CancellationToken.None);
            await repository.UpsertFileHashAsync(originalPath, "0000000000000000", null, CancellationToken.None);

            var duplicateOf = await repository.FindByHashAsync(
                "0000000000000003",
                Path.Combine(root, "Ivanov_banner_2026_2.png"),
                CancellationToken.None);

            Assert.Equal(originalPath, duplicateOf);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Orchestrator_records_duplicate_without_renaming_and_ignores_followup_event()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var options = new TestOptionsMonitor<ModuleOptions>(new ModuleOptions
            {
                DatabasePath = Path.Combine(root, "module.db"),
                DetectDuplicates = true,
                AutoAcceptTags = true,
                AllowedExtensions = [".png"],
                DuplicateHashDistanceThreshold = 5,
            });
            var repository = new SqliteModuleRepository(Options.Create(options.CurrentValue));
            await repository.InitializeAsync(CancellationToken.None);

            var orchestrator = new FileProcessingOrchestrator(
                new AlwaysValidRuleValidator(),
                new FixedElmaClient(new OrderData("1001", "Ivanov", "banner")),
                new EmptyGigaChatClient(),
                new FixedDuplicateDetector("0000000000000000"),
                new FixedDuplicateResolutionService(DuplicateResolutionAction.SaveAsNew),
                new FirstOrderSelectionService(),
                new NoopFileCorrectionService(),
                new AcceptingTagReviewService(),
                repository,
                new RecordingNotificationService(),
                new InMemoryViolationPolicy(),
                options,
                NullLogger<FileProcessingOrchestrator>.Instance);

            var originalPath = Path.Combine(root, "Ivanov_banner_2026_1.png");
            var duplicatePath = Path.Combine(root, "Ivanov_banner_2026_2.png");
            await File.WriteAllTextAsync(originalPath, "original image", CancellationToken.None);
            await File.WriteAllTextAsync(duplicatePath, "duplicate image", CancellationToken.None);

            await orchestrator.ProcessAsync(
                new FileDetectedEvent(originalPath, WatcherChangeTypes.Created, DateTime.UtcNow),
                CancellationToken.None);
            await orchestrator.ProcessAsync(
                new FileDetectedEvent(duplicatePath, WatcherChangeTypes.Created, DateTime.UtcNow),
                CancellationToken.None);

            Assert.True(File.Exists(duplicatePath));
            Assert.False(File.Exists(Path.Combine(root, "Ivanov_banner_2026_2(1).png")));

            await orchestrator.ProcessAsync(
                new FileDetectedEvent(duplicatePath, WatcherChangeTypes.Renamed, DateTime.UtcNow),
                CancellationToken.None);

            Assert.True(File.Exists(duplicatePath));
            Assert.False(File.Exists(Path.Combine(root, "Ivanov_banner_2026_2(1).png")));

            var rows = await ReadProcessingLogRowsAsync(options.CurrentValue.DatabasePath);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row =>
                row.Result == (int)ProcessingResult.DuplicateFound &&
                row.FilePath == duplicatePath &&
                row.DuplicateOf == originalPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Orchestrator_processes_same_path_again_when_file_content_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var options = new TestOptionsMonitor<ModuleOptions>(new ModuleOptions
            {
                DatabasePath = Path.Combine(root, "module.db"),
                DetectDuplicates = true,
                AutoAcceptTags = true,
                AllowedExtensions = [".png"],
                DuplicateHashDistanceThreshold = 5,
            });
            var repository = new SqliteModuleRepository(Options.Create(options.CurrentValue));
            await repository.InitializeAsync(CancellationToken.None);

            var orchestrator = new FileProcessingOrchestrator(
                new AlwaysValidRuleValidator(),
                new FixedElmaClient(new OrderData("1001", "Ivanov", "banner")),
                new EmptyGigaChatClient(),
                new FixedDuplicateDetector("0000000000000000"),
                new FixedDuplicateResolutionService(DuplicateResolutionAction.SaveAsNew),
                new FirstOrderSelectionService(),
                new NoopFileCorrectionService(),
                new AcceptingTagReviewService(),
                repository,
                new RecordingNotificationService(),
                new InMemoryViolationPolicy(),
                options,
                NullLogger<FileProcessingOrchestrator>.Instance);

            var filePath = Path.Combine(root, "Ivanov_banner_2026_1.png");
            await File.WriteAllTextAsync(filePath, "first image", CancellationToken.None);
            File.SetLastWriteTimeUtc(filePath, new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc));

            await orchestrator.ProcessAsync(
                new FileDetectedEvent(filePath, WatcherChangeTypes.Created, DateTime.UtcNow),
                CancellationToken.None);

            await File.WriteAllTextAsync(filePath, "first image with a new drawn circle", CancellationToken.None);
            File.SetLastWriteTimeUtc(filePath, new DateTime(2026, 5, 8, 10, 5, 0, DateTimeKind.Utc));

            await orchestrator.ProcessAsync(
                new FileDetectedEvent(filePath, WatcherChangeTypes.Changed, DateTime.UtcNow),
                CancellationToken.None);

            var rows = await ReadProcessingLogRowsAsync(options.CurrentValue.DatabasePath);
            Assert.Equal(2, rows.Count(row => row.FilePath == filePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Validator_reports_configuration_error_for_invalid_regex_without_throwing()
    {
        var options = new TestOptionsMonitor<ModuleOptions>(new ModuleOptions
        {
            RootDirectory = Path.GetTempPath(),
            ValidateFileName = true,
            ValidatePath = false,
            FileNameRegexPattern = "^[z-a]+$",
        });
        var validator = new RegexFileRuleValidator(options);

        var result = validator.Validate("abracadabra.png", null);

        Assert.True(result.HasViolations);
        Assert.Contains(result.FailureReasons, reason => reason.Contains("regex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Smart_search_finds_english_tags_from_russian_query()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        await CreateLogDatabaseAsync(dbPath);
        var service = new LogQueryService();

        var rows = await service.GetRecentAsync(dbPath, "синий баннер", 10, CancellationToken.None);

        if (rows.Count == 0)
        {
            rows = await service.GetRecentAsync(dbPath, "blue", 10, CancellationToken.None);
        }

        var row = Assert.Single(rows);
        Assert.Equal("Ivanov_Banner_2026_1.png", row.FileName);
    }

    [Fact]
    public async Task Smart_search_finds_legacy_json_tag_split_by_colon()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        await CreateLegacyLogDatabaseAsync(dbPath);
        var service = new LogQueryService();

        var rows = await service.GetRecentAsync(dbPath, "синий", 10, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Sidorov_logo_2026_1.png", row.FileName);
        Assert.Contains(row.NormalizedTags, tag =>
            tag.Key == "dominant_colors" &&
            tag.Value.Contains("синий", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Smart_search_reads_saved_file_tags_table()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        await CreateStoredTagLogDatabaseAsync(dbPath);
        var service = new LogQueryService();

        var rows = await service.GetRecentAsync(dbPath, "СЃРёРЅРёР№", 10, CancellationToken.None);

        if (rows.Count == 0)
        {
            rows = await service.GetRecentAsync(dbPath, "blue", 10, CancellationToken.None);
        }

        var row = Assert.Single(rows);
        Assert.Equal("Petrov_logo_2026_1.png", row.FileName);
        Assert.Contains(row.NormalizedTags, tag =>
            tag.Key == "dominant_colors" &&
            tag.Value.Contains("blue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Smart_search_matches_transliterated_file_name()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        await CreateStoredTagLogDatabaseAsync(dbPath);
        var service = new LogQueryService();

        var rows = await service.GetRecentAsync(dbPath, "\u041f\u0435\u0442\u0440\u043e\u0432", 10, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Petrov_logo_2026_1.png", row.FileName);
    }

    private static async Task CreateLogDatabaseAsync(string dbPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await connection.OpenAsync();

        await using var create = connection.CreateCommand();
        create.CommandText = """
CREATE TABLE file_tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL,
    tag_key TEXT NOT NULL,
    tag_value TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE processing_log (
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
""";
        await create.ExecuteNonQueryAsync();

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
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
    'Ivanov_Banner_2026_1.png',
    'D:\Design\Ivanov\banner\Ivanov_Banner_2026_1.png',
    'tester',
    '2026-05-01T00:00:00Z',
    0,
    0,
    'OK',
    '',
    '1001',
    '[{"Key":"color","Value":"blue"},{"Key":"category","Value":"banner"}]');
""";
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task CreateLegacyLogDatabaseAsync(string dbPath)
    {
        await CreateLogDatabaseSchemaAsync(dbPath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        await connection.OpenAsync();

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
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
    'Sidorov_logo_2026_1.png',
    'D:\Design\Sidorov\logo\Sidorov_logo_2026_1.png',
    'tester',
    '2026-05-01T00:00:00Z',
    0,
    0,
    'OK',
    '',
    '1003',
    $tagsJson);
""";
        insert.Parameters.AddWithValue("$tagsJson", """"
[{"Key":"{""description""","Value":"Логотип компании, ""tags"":[{""key"":""dominant_colors"",""value"":""синий, белый, голубой""},{""key"":""object_type"",""value"":""логотип""}]}"}]
"""");
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task CreateStoredTagLogDatabaseAsync(string dbPath)
    {
        await CreateLogDatabaseSchemaAsync(dbPath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        await connection.OpenAsync();

        await using var insertLog = connection.CreateCommand();
        insertLog.CommandText = """
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
    'Petrov_logo_2026_1.png',
    'D:\Design\Petrov\logo\Petrov_logo_2026_1.png',
    'tester',
    '2026-05-01T00:00:00Z',
    0,
    0,
    'OK',
    '',
    '1004',
    '[]');
""";
        await insertLog.ExecuteNonQueryAsync();

        await using var insertTag = connection.CreateCommand();
        insertTag.CommandText = """
INSERT INTO file_tags(file_path, tag_key, tag_value, created_at_utc)
VALUES (
    'D:\Design\Petrov\logo\Petrov_logo_2026_1.png',
    'dominant_colors',
    'blue, white',
    '2026-05-01T00:00:00Z');
""";
        await insertTag.ExecuteNonQueryAsync();
    }

    private static async Task CreateLogDatabaseSchemaAsync(string dbPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await connection.OpenAsync();

        await using var create = connection.CreateCommand();
        create.CommandText = """
CREATE TABLE file_tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL,
    tag_key TEXT NOT NULL,
    tag_value TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE processing_log (
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
""";
        await create.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string FileName, string FilePath, int Result, string DuplicateOf)>> ReadProcessingLogRowsAsync(string dbPath)
    {
        var rows = new List<(string FileName, string FilePath, int Result, string DuplicateOf)>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
SELECT file_name, file_path, result, duplicate_of
FROM processing_log
ORDER BY id;
""";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return rows;
    }

    private sealed class AlwaysValidRuleValidator : IFileRuleValidator
    {
        public FileValidationResult Validate(string filePath, OrderData? orderData) => FileValidationResult.Success();
    }

    private sealed class FixedElmaClient : IElmaClient
    {
        private readonly OrderData? _orderData;

        public FixedElmaClient(OrderData? orderData)
        {
            _orderData = orderData;
        }

        public Task<OrderData?> TryResolveOrderAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(_orderData);

        public Task<IReadOnlyCollection<OrderData>> GetOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<OrderData>>(_orderData is null ? Array.Empty<OrderData>() : new[] { _orderData });
    }

    private sealed class EmptyGigaChatClient : IGigaChatClient
    {
        public Task<IReadOnlyCollection<TagItem>> GenerateTagsAsync(string filePath, OrderData? orderData, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<TagItem>>(Array.Empty<TagItem>());
    }

    private sealed class FixedDuplicateDetector : IDuplicateDetector
    {
        private readonly string _hash;

        public FixedDuplicateDetector(string hash)
        {
            _hash = hash;
        }

        public Task<string> ComputePerceptualHashAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(_hash);
    }

    private sealed class FixedDuplicateResolutionService : IDuplicateResolutionService
    {
        private readonly DuplicateResolutionAction _action;

        public FixedDuplicateResolutionService(DuplicateResolutionAction action)
        {
            _action = action;
        }

        public Task<DuplicateResolutionAction> ResolveAsync(
            string currentFilePath,
            string duplicateFilePath,
            OrderData? orderData,
            CancellationToken cancellationToken) =>
            Task.FromResult(_action);
    }

    private sealed class FirstOrderSelectionService : IOrderSelectionService
    {
        public Task<OrderData?> SelectOrderAsync(
            string filePath,
            IReadOnlyCollection<OrderData> orders,
            CancellationToken cancellationToken) =>
            Task.FromResult(orders.FirstOrDefault());
    }

    private sealed class NoopFileCorrectionService : IFileCorrectionService
    {
        public Task<FileCorrectionAction> RequestCorrectionAsync(
            string rejectedFilePath,
            string recommendedDirectory,
            string recommendedFileName,
            string reason,
            CancellationToken cancellationToken) =>
            Task.FromResult(FileCorrectionAction.None);
    }

    private sealed class AcceptingTagReviewService : ITagReviewService
    {
        public Task<bool> RequestTagApprovalAsync(
            string filePath,
            IReadOnlyCollection<TagItem> tags,
            OrderData? orderData,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class RecordingNotificationService : IFileNotificationService
    {
        public List<(string Title, string Message)> Notifications { get; } = new();

        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
        {
            Notifications.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
