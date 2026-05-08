using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MediaModule.Desktop.Models;
using MediaModule.Desktop.Services;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;

namespace MediaModule.Desktop;

public partial class MainWindow : Window
{
    private enum AppScreen
    {
        Dashboard,
        ManualCheck,
        Settings,
        Journal,
        Playground,
        Search,
    }

    private enum UserRole
    {
        Administrator,
        Designer,
    }

    private readonly ObservableCollection<ProcessingLogRow> _logs = new();
    private readonly ObservableCollection<TagRow> _selectedTags = new();
    private readonly ObservableCollection<TagRow> _gigachatTags = new();
    private readonly ObservableCollection<ProcessingLogRow> _searchResults = new();
    private readonly ObservableCollection<ProcessingLogRow> _journalLogs = new();
    private readonly ObservableCollection<string> _searchHistory = new();
    private readonly ObservableCollection<ManualFileCheckRow> _manualFiles = new();
    private readonly DispatcherTimer _refreshTimer = new();

    private readonly WorkerSettingsService _settingsService;
    private readonly LogQueryService _logQueryService = new();
    private readonly GigachatPlaygroundService _gigachatPlaygroundService = new();
    private readonly Dictionary<TextBlock, ProcessingLogRow> _summaryRowsByTextBlock = new();
    private readonly Dictionary<Border, ProcessingLogRow> _tagRowsByCard = new();

    private const string AdminPassword = "123";
    private UserRole _currentRole = UserRole.Administrator;
    private bool _adminUnlocked = true;
    private bool _validateFileName = true;
    private bool _validatePath = true;
    private bool _detectDuplicates = true;
    private bool _settingsDirty;
    private bool _loadingSettings;
    private ProcessingLogRow? _detailsRow;
    private ProcessingLogRow? _selectedSearchResult;
    private string _gigaSelectedImagePath = string.Empty;
    private bool _refreshingLogs;
    private string _currentSearchFilter = string.Empty;
    private bool _manualProcessing;
    private CancellationTokenSource? _manualProcessingCts;

    public MainWindow()
    {
        InitializeComponent();

        LogsDataGrid.ItemsSource = _logs;
        JournalLogsDataGridMirror.ItemsSource = _journalLogs;
        TagsDataGrid.ItemsSource = _selectedTags;
        GigachatTagsDataGrid.ItemsSource = _gigachatTags;
        SearchResultsListBox.ItemsSource = _searchResults;
        ManualFilesDataGrid.ItemsSource = _manualFiles;

        var settingsPath = FindWorkerSettingsPath();
        _settingsService = new WorkerSettingsService(settingsPath);

        LoadSavedRole();
        LoadSearchHistory();
        LoadSettingsIntoUi();
        UpdateDashboard(Array.Empty<ProcessingLogRow>());
        _ = RefreshLogsAsync();
        StartAutoRefresh();

        ShowScreen(AppScreen.Dashboard);
        UpdateRoleUi();
        UpdateSearchPlaceholder();
        UpdateJournalFilterPlaceholder();
        UpdateManualCheckUi();
    }

    private void StartAutoRefresh()
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(3);
        _refreshTimer.Tick += async (_, _) => await RefreshLogsAsync(preserveSelection: true);
        _refreshTimer.Start();
    }

    private void LoadSettingsIntoUi()
    {
        try
        {
            _loadingSettings = true;
            if (!_settingsService.Exists())
            {
                StatusTextBlock.Text = $"Не найден appsettings worker: {_settingsService.SettingsPath}";
                return;
            }

            var snapshot = _settingsService.Load();

            RootDirectoryTextBox.Text = snapshot.RootDirectory;
            RegexTextBox.Text = snapshot.FileNameRegexPattern;
            _validateFileName = snapshot.ValidateFileName;
            _validatePath = snapshot.ValidatePath;
            _detectDuplicates = snapshot.DetectDuplicates;
            MonitoredDirectoriesTextBox.Text = snapshot.MonitoredDirectoriesMultiline;
            DatabasePathConfigTextBox.Text = snapshot.DatabasePath;
            AutoAcceptTagsCheckBox.IsChecked = snapshot.AutoAcceptTags;
            OrdersTextBox.Text = snapshot.OrdersMultiline;
            SaveRootDirectoryButton.Visibility = Visibility.Collapsed;
            UpdateRuleButtons();
            _settingsDirty = false;

            DatabasePathTextBlock.Text = $"SQLite DB: {snapshot.ResolvedDatabasePath}";
            StatusTextBlock.Text = "Настройки загружены.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка загрузки настроек: {ex.Message}";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async Task RefreshLogsAsync(bool preserveSelection = false)
    {
        if (_refreshingLogs)
        {
            return;
        }

        _refreshingLogs = true;
        try
        {
            var selectedId = preserveSelection
                ? (LogsDataGrid.SelectedItem as ProcessingLogRow)?.Id
                : null;
            var current = BuildSnapshotFromUi();
            var resolvedDbPath = _settingsService.ResolveDatabasePath(current.DatabasePath);

            DatabasePathTextBlock.Text = $"SQLite DB: {resolvedDbPath}";

            var logs = (await _logQueryService.GetRecentAsync(
                resolvedDbPath,
                string.Empty,
                300,
                CancellationToken.None))
                .ToList();

            if (logs.Count == 0)
            {
                logs.Add(CreateDemoLogRow());
            }

            _logs.Clear();
            foreach (var row in logs)
            {
                _logs.Add(row);
            }

            _selectedTags.Clear();
            UpdateDashboard(logs);
            ApplyJournalView(selectedId);
            UpdateSearchResults(logs, LogFilterTextBox.Text.Trim());

            if (_logs.Count > 0)
            {
                var selectedRow = selectedId is null
                    ? _logs[0]
                    : _logs.FirstOrDefault(row => row.Id == selectedId.Value) ?? _logs[0];
                LogsDataGrid.SelectedItem = selectedRow;
                if (_journalLogs.Contains(selectedRow))
                {
                    JournalLogsDataGridMirror.SelectedItem = selectedRow;
                }
                UpdateSelectedTags(selectedRow);
            }
            else
            {
                LogsDataGrid.SelectedIndex = -1;
                JournalLogsDataGridMirror.SelectedIndex = -1;
            }
            if (!preserveSelection)
            {
                StatusTextBlock.Text = $"Журнал обновлен. Записей: {_logs.Count}.";
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка чтения журнала: {ex.Message}";
        }
        finally
        {
            _refreshingLogs = false;
        }
    }

    private static ProcessingLogRow CreateDemoLogRow()
    {
        return new ProcessingLogRow
        {
            Id = -1,
            FileId = -1,
            OperationTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            FileName = "ivanov_banner_2026_1.png",
            FilePath = @"C:\Demo\Design\Ivanov\banner\ivanov_banner_2026_1.png",
            Result = "Успешно",
            ErrorIgnored = false,
            Message = "Демо-запись для интерфейса.",
            OrderId = "DEMO-001",
            DuplicateOf = string.Empty,
            TagsJson = """
                [
                  { "Key": "client", "Value": "Ivanov" },
                  { "Key": "product", "Value": "banner" },
                  { "Key": "year", "Value": "2026" }
                ]
                """,
            NormalizedTags = new[]
            {
                new TagRow { Key = "client", Value = "Ivanov" },
                new TagRow { Key = "product", Value = "banner" },
                new TagRow { Key = "year", Value = "2026" },
            },
        };
    }

    private WorkerSettingsSnapshot BuildSnapshotFromUi()
    {
        return new WorkerSettingsSnapshot
        {
            RootDirectory = RootDirectoryTextBox.Text.Trim(),
            FileNameRegexPattern = RegexTextBox.Text.Trim(),
            ValidateFileName = _validateFileName,
            ValidatePath = _validatePath,
            DetectDuplicates = _detectDuplicates,
            MonitoredDirectoriesMultiline = MonitoredDirectoriesTextBox.Text,
            AutoAcceptTags = AutoAcceptTagsCheckBox.IsChecked == true,
            DatabasePath = DatabasePathConfigTextBox.Text.Trim(),
            OrdersMultiline = OrdersTextBox.Text,
        };
    }

    private void UpdateRuleButtons()
    {
        ValidateFileNameRuleCheckBox.IsChecked = _validateFileName;
        ValidatePathRuleCheckBox.IsChecked = _validatePath;
        DetectDuplicatesRuleCheckBox.IsChecked = _detectDuplicates;
    }

    private void SettingsField_Changed(object sender, RoutedEventArgs e)
    {
        MarkSettingsDirty();
    }

    private void MarkSettingsDirty()
    {
        if (_loadingSettings || StatusTextBlock is null)
        {
            return;
        }

        _settingsDirty = true;
        StatusTextBlock.Text = "Настройки изменены. Сохраните изменения перед выходом.";
    }

    private void SaveCurrentSettings(string successMessage)
    {
        var snapshot = BuildSnapshotFromUi();
        _settingsService.Save(snapshot);
        LoadSettingsIntoUi();
        _settingsDirty = false;
        StatusTextBlock.Text = successMessage;
    }

    private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLogsAsync();
    }

    private void LogsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = LogsDataGrid.SelectedItem as ProcessingLogRow;
        UpdateSelectedTags(row);
        if (row is not null && _journalLogs.Contains(row))
        {
            JournalLogsDataGridMirror.SelectedItem = row;
        }
    }

    private void JournalLogsDataGridMirror_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = JournalLogsDataGridMirror.SelectedItem as ProcessingLogRow;
        UpdateSelectedTags(row);
        if (row is not null)
        {
            LogsDataGrid.SelectedItem = row;
        }
    }

    private void JournalLogsDataGridMirror_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is not null &&
            JournalLogsDataGridMirror.SelectedItem is ProcessingLogRow row)
        {
            ShowOperationDetails(row);
        }
    }

    private void ApplyJournalView(int? selectedId = null)
    {
        if (JournalLogsDataGridMirror is null)
        {
            return;
        }

        var rows = _logs.AsEnumerable();
        var query = JournalFilterTextBox?.Text.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(query))
        {
            rows = rows.Where(row => _logQueryService.MatchesFilter(row, query));
        }

        var resultFilter = GetSelectedTag(JournalResultFilterComboBox);
        if (!string.IsNullOrWhiteSpace(resultFilter) && resultFilter != "all")
        {
            rows = rows.Where(row => string.Equals(row.Result, resultFilter, StringComparison.OrdinalIgnoreCase));
        }

        var tagFilter = GetSelectedTag(JournalTagFilterComboBox);
        rows = tagFilter switch
        {
            "tagged" => rows.Where(static row => row.NormalizedTags.Count > 0),
            "untagged" => rows.Where(static row => row.NormalizedTags.Count == 0),
            _ => rows,
        };

        rows = GetSelectedTag(JournalSortComboBox) switch
        {
            "time_asc" => rows.OrderBy(static row => ParseOperationTime(row.OperationTimeUtc)),
            "file_asc" => rows.OrderBy(static row => row.FileName, StringComparer.OrdinalIgnoreCase),
            "file_desc" => rows.OrderByDescending(static row => row.FileName, StringComparer.OrdinalIgnoreCase),
            "result_asc" => rows.OrderBy(static row => row.Result, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static row => ParseOperationTime(row.OperationTimeUtc)),
            "tags_desc" => rows.OrderByDescending(static row => row.NormalizedTags.Count)
                .ThenByDescending(static row => ParseOperationTime(row.OperationTimeUtc)),
            _ => rows.OrderByDescending(static row => ParseOperationTime(row.OperationTimeUtc)),
        };

        var filtered = rows.ToList();
        _journalLogs.Clear();
        foreach (var row in filtered)
        {
            _journalLogs.Add(row);
        }

        if (JournalCountTextBlock is not null)
        {
            JournalCountTextBlock.Text = $"Показано: {_journalLogs.Count} из {_logs.Count}";
        }

        if (_journalLogs.Count == 0)
        {
            JournalLogsDataGridMirror.SelectedIndex = -1;
            return;
        }

        var selectedRow = selectedId is null
            ? _journalLogs[0]
            : _journalLogs.FirstOrDefault(row => row.Id == selectedId.Value) ?? _journalLogs[0];
        JournalLogsDataGridMirror.SelectedItem = selectedRow;
    }

    private static string GetSelectedTag(System.Windows.Controls.ComboBox? comboBox)
    {
        return comboBox?.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset ParseOperationTime(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private void JournalFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (JournalLogsDataGridMirror is null)
        {
            return;
        }

        UpdateJournalFilterPlaceholder();
        ApplyJournalView((JournalLogsDataGridMirror.SelectedItem as ProcessingLogRow)?.Id);
    }

    private void UpdateJournalFilterPlaceholder()
    {
        if (JournalFilterPlaceholder is null || JournalFilterTextBox is null)
        {
            return;
        }

        JournalFilterPlaceholder.Visibility = string.IsNullOrWhiteSpace(JournalFilterTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ResetJournalFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        JournalFilterTextBox.Clear();
        JournalResultFilterComboBox.SelectedIndex = 0;
        JournalTagFilterComboBox.SelectedIndex = 0;
        JournalSortComboBox.SelectedIndex = 0;
        ApplyJournalView();
    }

    private static T? FindParent<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void UpdateSelectedTags(ProcessingLogRow? row)
    {
        _selectedTags.Clear();

        if (row is null)
        {
            return;
        }

        var parsed = GetTags(row);
        foreach (var tag in parsed)
        {
            _selectedTags.Add(new TagRow { Key = DisplayTagKey(tag.Key), Value = tag.Value });
        }
    }

    private void ShowOperationDetails(ProcessingLogRow row)
    {
        _detailsRow = row;

        OperationFileRun.Text = row.FileName;
        OperationResultRun.Text = row.Result;
        OperationDateRun.Text = row.OperationTimeDisplay;
        OperationMessageRun.Text = string.IsNullOrWhiteSpace(row.Message) ? "Нет сообщения" : row.Message;
        OperationOrderRun.Text = string.IsNullOrWhiteSpace(row.OrderId) ? "Не указан" : row.OrderId;
        OperationPathRun.Text = string.IsNullOrWhiteSpace(row.FilePath) ? "Не указано" : row.FilePath;
        OperationDuplicateRun.Text = row.DuplicateOf;
        OperationDuplicateLabelTextBlock.Visibility = string.IsNullOrWhiteSpace(row.DuplicateOf)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OperationDuplicateTextBlock.Visibility = string.IsNullOrWhiteSpace(row.DuplicateOf)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OperationTagsRun.Text = BuildTagsText(GetTags(row));
        OpenOperationFolderButton.Visibility = string.IsNullOrWhiteSpace(row.FilePath)
            ? Visibility.Collapsed
            : Visibility.Visible;

        OperationDetailsOverlay.Visibility = Visibility.Visible;
    }

    private static string BuildTagsText(IReadOnlyCollection<TagRow> tags)
    {
        if (tags.Count == 0)
        {
            return "Нет тегов";
        }

        return string.Join(Environment.NewLine, tags.Select(static tag => $"{DisplayTagKey(tag.Key)}: {tag.Value}"));
    }

    private void CloseOperationDetails()
    {
        OperationDetailsOverlay.Visibility = Visibility.Collapsed;
        _detailsRow = null;
    }

    private void CloseOperationDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOperationDetails();
    }

    private void OperationDetailsOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == OperationDetailsOverlay)
        {
            CloseOperationDetails();
        }
    }

    private void OperationDetailsDialog_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OpenOperationFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsRow is null)
        {
            return;
        }

        OpenFileLocation(_detailsRow.FilePath);
    }

    private void OpenFileLocation(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
                return;
            }

            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
                return;
            }

            StatusTextBlock.Text = "Папка файла не найдена.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Не удалось открыть папку: {ex.Message}";
        }
    }

    private void UpdateDashboard(IReadOnlyList<ProcessingLogRow> logs)
    {
        var hasLogs = logs.Count > 0;
        _summaryRowsByTextBlock.Clear();
        var processed = logs
            .Where(static row => row.Result is "Успешно" or "Сохранено с нарушением" or "Дубликат найден" or "Дубликат переименован" or "Исправлено пользователем")
            .ToList();
        var issues = logs
            .Where(static row => row.Result is "Заблокировано" or "Ошибка")
            .ToList();
        var duplicates = logs
            .Where(static row => row.Result is "Дубликат найден" or "Дубликат переименован")
            .ToList();

        UpdateSummaryCard(
            ProcessedCountTextBlock,
            new[] { ProcessedFile1TextBlock, ProcessedFile2TextBlock, ProcessedFile3TextBlock },
            new[] { ProcessedRow1Panel, ProcessedRow2Panel, ProcessedRow3Panel },
            processed,
            "Пусто");

        UpdateSummaryCard(
            IssuesCountTextBlock,
            new[] { IssueFile1TextBlock, IssueFile2TextBlock, IssueFile3TextBlock },
            new[] { IssueRow1Panel, IssueRow2Panel, IssueRow3Panel },
            issues,
            "Пусто");

        UpdateSummaryCard(
            DuplicatesCountTextBlock,
            new[] { DuplicateFile1TextBlock, DuplicateFile2TextBlock },
            new[] { DuplicateRow1Panel, DuplicateRow2Panel },
            duplicates,
            "Пусто");

        StatusCardsGrid.Visibility = Visibility.Visible;
        StatusEmptyTextBlock.Visibility = Visibility.Collapsed;

        UpdateDashboardMetrics(logs, processed);
        UpdateRecentTagCards(logs);
    }

    private void UpdateDashboardMetrics(IReadOnlyList<ProcessingLogRow> logs, IReadOnlyList<ProcessingLogRow> processed)
    {
        TotalOperationsTextBlock.Text = logs.Count.ToString();

        var successRate = logs.Count == 0
            ? 0
            : (int)Math.Round(processed.Count * 100d / logs.Count);
        SuccessRateTextBlock.Text = $"{successRate}%";

        var tagCounts = logs
            .Select(GetDashboardTagCount)
            .ToList();
        TaggedFilesTextBlock.Text = tagCounts.Count(static count => count > 0).ToString();
        AverageTagsTextBlock.Text = tagCounts.Count == 0
            ? "0"
            : Math.Round(tagCounts.Average(), 1).ToString("0.#");

        var lastOperation = logs
            .OrderByDescending(static row => ParseOperationTime(row.OperationTimeUtc))
            .FirstOrDefault();
        LastOperationTextBlock.Text = lastOperation is null
            ? "Нет данных"
            : lastOperation.OperationTimeDisplay;
        LastOperationResultTextBlock.Text = lastOperation?.Result ?? string.Empty;
    }

    private int GetDashboardTagCount(ProcessingLogRow row)
    {
        return row.NormalizedTags.Count > 0
            ? row.NormalizedTags.Count
            : _logQueryService.ParseTags(row.TagsJson).Count;
    }

    private void UpdateSummaryCard(
        TextBlock countBlock,
        IReadOnlyList<TextBlock> itemBlocks,
        IReadOnlyList<DockPanel> rowPanels,
        IReadOnlyList<ProcessingLogRow> rows,
        string emptyText)
    {
        countBlock.Text = rows.Count.ToString();

        if (rows.Count == 0)
        {
            for (var index = 0; index < rowPanels.Count; index++)
            {
                rowPanels[index].Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
                itemBlocks[index].Text = index == 0 ? emptyText : string.Empty;
                ConfigureSummaryFileLink(itemBlocks[index], null);
            }

            return;
        }

        for (var index = 0; index < itemBlocks.Count; index++)
        {
            var hasRow = index < rows.Count;
            rowPanels[index].Visibility = hasRow ? Visibility.Visible : Visibility.Collapsed;
            itemBlocks[index].Text = hasRow ? rows[index].FileName : string.Empty;
            ConfigureSummaryFileLink(itemBlocks[index], hasRow ? rows[index] : null);
        }
    }

    private void ConfigureSummaryFileLink(TextBlock textBlock, ProcessingLogRow? row)
    {
        textBlock.TextDecorations = null;
        textBlock.ToolTip = null;
        textBlock.Cursor = null;
        textBlock.MouseLeftButtonUp -= SummaryFileTextBlock_MouseLeftButtonUp;
        _summaryRowsByTextBlock.Remove(textBlock);

        if (row is null || string.IsNullOrWhiteSpace(row.FilePath))
        {
            return;
        }

        _summaryRowsByTextBlock[textBlock] = row;
        textBlock.ToolTip = string.IsNullOrWhiteSpace(row.DuplicateOf)
            ? row.FilePath
            : $"Файл: {row.FilePath}{Environment.NewLine}Оригинал: {row.DuplicateOf}";
        textBlock.Cursor = System.Windows.Input.Cursors.Hand;
        textBlock.TextDecorations = TextDecorations.Underline;
        textBlock.MouseLeftButtonUp += SummaryFileTextBlock_MouseLeftButtonUp;
    }

    private void SummaryFileTextBlock_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBlock textBlock && _summaryRowsByTextBlock.TryGetValue(textBlock, out var row))
        {
            OpenFileLocation(row.FilePath);
        }
    }

    private void UpdateRecentTagCards(IReadOnlyList<ProcessingLogRow> logs)
    {
        var cards = GetRecentTagCardBlocks();
        if (cards.Count == 0)
        {
            return;
        }

        _tagRowsByCard.Clear();
        foreach (var card in cards)
        {
            card.Card.MouseLeftButtonUp -= TagCard_MouseLeftButtonUp;
            card.Card.Cursor = null;
            card.Card.ToolTip = null;
        }

        var taggedLogs = logs
            .Select(row => new
            {
                Row = row,
                Tags = GetTags(row),
            })
            .Where(x => x.Tags.Count > 0)
            .Take(cards.Count)
            .ToList();

        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];

            if (index >= taggedLogs.Count)
            {
                card.File.Text = "Тегов пока нет";
                card.Summary.Text = "Сохраните файл, чтобы здесь появились последние теги.";
                card.Action.Text = string.Empty;
                continue;
            }

            var entry = taggedLogs[index];
            card.File.Text = entry.Row.FileName;
            card.Summary.Text = BuildTagSummary(entry.Tags);
            card.Action.Text = "Открыть детали";
            card.Card.Cursor = System.Windows.Input.Cursors.Hand;
            card.Card.ToolTip = "Открыть подробности файла";
            card.Card.MouseLeftButtonUp += TagCard_MouseLeftButtonUp;
            _tagRowsByCard[card.Card] = entry.Row;
        }
    }

    private void TagCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border card && _tagRowsByCard.TryGetValue(card, out var row))
        {
            ShowOperationDetails(row);
        }
    }

    private void CreateTagsCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowScreen(AppScreen.Playground);
        StatusTextBlock.Text = "Откройте изображение и сгенерируйте теги в GigaChat Playground.";
    }

    private static string BuildTagSummary(IReadOnlyCollection<TagRow> tags)
    {
        var parts = tags
            .Take(3)
            .Select(static tag => $"{tag.Key}={tag.Value}")
            .ToList();

        if (parts.Count == 0)
        {
            return "В этой записи нет тегов.";
        }

        if (tags.Count > parts.Count)
        {
            parts.Add($"+{tags.Count - parts.Count} еще");
        }

        return string.Join(", ", parts);
    }

    private IReadOnlyList<(Border Card, TextBlock File, TextBlock Summary, TextBlock Action)> GetRecentTagCardBlocks()
    {
        if (TagCardsGrid is null)
        {
            return Array.Empty<(Border Card, TextBlock File, TextBlock Summary, TextBlock Action)>();
        }

        var result = new List<(Border Card, TextBlock File, TextBlock Summary, TextBlock Action)>();

        foreach (var border in TagCardsGrid.Children.OfType<Border>().Take(4))
        {
            if (border.Child is not StackPanel stack ||
                stack.Children.Count < 3 ||
                stack.Children[0] is not DockPanel header ||
                header.Children.Count < 2 ||
                header.Children[1] is not TextBlock fileBlock ||
                stack.Children[1] is not TextBlock summaryBlock ||
                stack.Children[2] is not TextBlock actionBlock)
            {
                continue;
            }

            result.Add((border, fileBlock, summaryBlock, actionBlock));
        }

        return result;
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings("Настройки сохранены.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка сохранения: {ex.Message}";
        }
    }

    private void ReloadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        StatusTextBlock.Text = "Настройки сброшены к сохраненному состоянию.";
    }

    private void BrowseRootDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите корневую директорию модуля",
            InitialDirectory = Directory.Exists(RootDirectoryTextBox.Text)
                ? RootDirectoryTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        RootDirectoryTextBox.Text = dialog.SelectedPath;
        SaveRootDirectoryButton.Visibility = Visibility.Visible;
        MarkSettingsDirty();
        StatusTextBlock.Text = "Выбрана новая корневая директория. Нажмите \"Сохранить\".";
    }

    private void SaveRootDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings("Корневая директория сохранена.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка сохранения директории: {ex.Message}";
        }
    }

    private void ValidateFileNameRuleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _validateFileName = ValidateFileNameRuleCheckBox.IsChecked == true;
        MarkSettingsDirty();
    }

    private void ValidatePathRuleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _validatePath = ValidatePathRuleCheckBox.IsChecked == true;
        MarkSettingsDirty();
    }

    private void DetectDuplicatesRuleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _detectDuplicates = DetectDuplicatesRuleCheckBox.IsChecked == true;
        MarkSettingsDirty();
    }

    private void SaveRuleChange(string successMessage)
    {
        try
        {
            UpdateRuleButtons();
            SaveCurrentSettings(successMessage);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка сохранения правила: {ex.Message}";
            LoadSettingsIntoUi();
        }
    }

    private async void GenerateTagsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_gigaSelectedImagePath) || !File.Exists(_gigaSelectedImagePath))
            {
                StatusTextBlock.Text = "Выберите изображение для тестирования GigaChat.";
                return;
            }

            _gigachatTags.Clear();
            StatusTextBlock.Text = "Отправляю изображение в реальный GigaChat...";
            GigaRequestTextBox.Text = _gigachatPlaygroundService.BuildRequestPreview(
                _gigaSelectedImagePath,
                GigaOrderIdTextBox.Text,
                _settingsService.Load());

            var generated = await _gigachatPlaygroundService.GenerateRealTagsAsync(
                _gigaSelectedImagePath,
                GigaOrderIdTextBox.Text,
                _settingsService.Load(),
                CancellationToken.None);

            foreach (var tag in generated)
            {
                _gigachatTags.Add(new TagRow { Key = DisplayTagKey(tag.Key), Value = tag.Value });
            }

            StatusTextBlock.Text = $"GigaChat вернул тегов: {_gigachatTags.Count}. Запрос показан в Playground.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Ошибка Playground GigaChat: {BuildExceptionMessage(ex)}";
        }
    }

    private static string BuildExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages.Distinct());
    }

    private void ChooseGigaImageButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Выберите изображение для GigaChat Playground",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|Все файлы|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        _gigaSelectedImagePath = dialog.FileName;
        GigaSelectedFileTextBlock.Text = Path.GetFileName(_gigaSelectedImagePath);
        GigaSelectedFileTextBlock.ToolTip = _gigaSelectedImagePath;
        LoadGigaPlaygroundPreview(_gigaSelectedImagePath);
        GigaRequestTextBox.Text = "Нажмите Сгенерировать, чтобы увидеть запрос.";
        _gigachatTags.Clear();
        StatusTextBlock.Text = $"Выбрано изображение: {Path.GetFileName(_gigaSelectedImagePath)}.";
    }

    private void LoadGigaPlaygroundPreview(string imagePath)
    {
        GigaPreviewImage.Source = null;

        if (!File.Exists(imagePath))
        {
            GigaPreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(imagePath);
            image.EndInit();
            image.Freeze();

            GigaPreviewImage.Source = image;
            GigaPreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            GigaPreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void DashboardNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(AppScreen.Dashboard);
    }

    private void BreadcrumbHomeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(AppScreen.Dashboard);
    }

    private void ManualCheckNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(AppScreen.ManualCheck);
    }

    private void AddManualFilesButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Выберите графические файлы",
            Filter = BuildManualFileDialogFilter(),
            Multiselect = true,
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            AddManualFiles(dialog.FileNames);
        }
    }

    private void AddManualFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку с графическими файлами",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var allowedExtensions = GetManualAllowedExtensions();
        var files = Directory.EnumerateFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories)
            .Where(path => allowedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AddManualFiles(files);
    }

    private void ClearManualFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manualProcessing)
        {
            return;
        }

        _manualFiles.Clear();
        ManualCheckProgressBar.Value = 0;
        ManualCheckCurrentTextBlock.Text = "Очередь пуста";
        ResetManualPreview();
        UpdateManualCheckUi();
    }

    private void ManualFilesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ManualFilesDataGrid.SelectedItem is ManualFileCheckRow row)
        {
            ShowManualPreview(row, "Выбран файл из очереди");
        }
        else
        {
            ResetManualPreview();
        }
    }

    private async void StartManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await RunManualCheckAsync();
    }

    private void CancelManualCheckButton_Click(object sender, RoutedEventArgs e)
    {
        _manualProcessingCts?.Cancel();
        ManualCheckCurrentTextBlock.Text = "Останавливаю после текущего файла...";
    }

    private async Task RunManualCheckAsync()
    {
        if (_manualProcessing || _manualFiles.Count == 0)
        {
            return;
        }

        _manualProcessing = true;
        _manualProcessingCts = new CancellationTokenSource();
        UpdateManualCheckUi();

        var cancellationToken = _manualProcessingCts.Token;
        var progress = new Progress<ManualProcessingNotification>(notification =>
        {
            ManualCheckCurrentTextBlock.Text = $"{notification.Title}: {notification.Message.Replace(Environment.NewLine, " ")}";
            StatusTextBlock.Text = notification.Title;
        });

        try
        {
            var processor = new ManualFileProcessingService(_settingsService, progress);
            await processor.InitializeAsync(cancellationToken);

            var rows = _manualFiles.ToList();
            var total = rows.Count;
            var completed = 0;

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(row.FilePath))
                {
                    row.Status = "Пропущен";
                    row.Message = "Файл не найден";
                    completed++;
                    ManualCheckProgressBar.Value = completed * 100d / total;
                    continue;
                }

                row.Status = "В обработке";
                row.Message = "CRM, правила, дубликаты, теги";
                ManualFilesDataGrid.SelectedItem = row;
                ManualFilesDataGrid.ScrollIntoView(row);
                ManualCheckCurrentTextBlock.Text = row.FileName;
                ShowManualPreview(row, "Идет проверка");

                try
                {
                    await processor.ProcessAsync(row.FilePath, cancellationToken);
                    row.Status = "Готово";
                    row.Message = "Записан в журнал";
                }
                catch (OperationCanceledException)
                {
                    row.Status = "Остановлен";
                    row.Message = "Проверка прервана";
                    throw;
                }
                catch (Exception ex)
                {
                    row.Status = "Ошибка";
                    row.Message = ex.Message;
                }
                finally
                {
                    completed++;
                    ManualCheckProgressBar.Value = completed * 100d / total;
                    await RefreshLogsAsync(preserveSelection: true);
                }
            }

            ManualCheckCurrentTextBlock.Text = $"Проверка завершена. Файлов: {completed}.";
            StatusTextBlock.Text = "Ручная проверка файлов завершена.";
        }
        catch (OperationCanceledException)
        {
            ManualCheckCurrentTextBlock.Text = "Проверка остановлена.";
            StatusTextBlock.Text = "Ручная проверка остановлена.";
        }
        catch (Exception ex)
        {
            ManualCheckCurrentTextBlock.Text = $"Ошибка запуска проверки: {ex.Message}";
            StatusTextBlock.Text = $"Ошибка ручной проверки: {ex.Message}";
        }
        finally
        {
            _manualProcessing = false;
            _manualProcessingCts?.Dispose();
            _manualProcessingCts = null;
            UpdateManualCheckUi();
            await RefreshLogsAsync(preserveSelection: true);
        }
    }

    private void AddManualFiles(IEnumerable<string> filePaths)
    {
        if (_manualProcessing)
        {
            return;
        }

        var allowedExtensions = GetManualAllowedExtensions();
        var existing = _manualFiles
            .Select(static row => row.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var path in filePaths)
        {
            if (!File.Exists(path) ||
                !allowedExtensions.Contains(Path.GetExtension(path)) ||
                !existing.Add(path))
            {
                continue;
            }

            _manualFiles.Add(new ManualFileCheckRow { FilePath = path });
            added++;
        }

        if (added > 0 && ManualFilesDataGrid.SelectedItem is null)
        {
            ManualFilesDataGrid.SelectedItem = _manualFiles[0];
            ShowManualPreview(_manualFiles[0], "Выбран файл из очереди");
        }

        UpdateManualCheckUi();
        ManualCheckCurrentTextBlock.Text = added == 0
            ? "Новые файлы не добавлены."
            : $"Добавлено файлов: {added}.";
        StatusTextBlock.Text = $"В очереди ручной проверки: {_manualFiles.Count}.";
    }

    private HashSet<string> GetManualAllowedExtensions()
    {
        try
        {
            return _settingsService.LoadModuleOptions().AllowedExtensions
                .Where(static extension => !string.IsNullOrWhiteSpace(extension))
                .Select(static extension => extension.StartsWith('.') ? extension : $".{extension}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png",
                ".jpg",
                ".jpeg",
                ".webp",
                ".bmp",
                ".tif",
                ".tiff",
            };
        }
    }

    private string BuildManualFileDialogFilter()
    {
        var patterns = GetManualAllowedExtensions()
            .OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
            .Select(static extension => $"*{extension}")
            .ToList();

        return patterns.Count == 0
            ? "Все файлы (*.*)|*.*"
            : $"Графические файлы ({string.Join("; ", patterns)})|{string.Join(";", patterns)}|Все файлы (*.*)|*.*";
    }

    private void UpdateManualCheckUi()
    {
        if (ManualQueueCountTextBlock is null)
        {
            return;
        }

        ManualQueueCountTextBlock.Text = $"{_manualFiles.Count} файлов";
        AddManualFilesButton.IsEnabled = !_manualProcessing;
        AddManualFolderButton.IsEnabled = !_manualProcessing;
        ClearManualFilesButton.IsEnabled = !_manualProcessing && _manualFiles.Count > 0;
        StartManualCheckButton.IsEnabled = !_manualProcessing && _manualFiles.Count > 0;
        CancelManualCheckButton.Visibility = _manualProcessing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowManualPreview(ManualFileCheckRow row, string status)
    {
        ManualPreviewImage.Source = null;
        ManualPreviewStatusTextBlock.Text = status;
        ManualPreviewFileNameTextBlock.Text = row.FileName;
        ManualPreviewPathTextBlock.Text = row.FilePath;

        if (!File.Exists(row.FilePath))
        {
            ManualPreviewPlaceholder.Text = "Файл не найден";
            ManualPreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(row.FilePath);
            image.EndInit();
            image.Freeze();

            ManualPreviewImage.Source = image;
            ManualPreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ManualPreviewPlaceholder.Text = "Предпросмотр недоступен";
            ManualPreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ResetManualPreview()
    {
        ManualPreviewImage.Source = null;
        ManualPreviewPlaceholder.Text = "Предпросмотр недоступен";
        ManualPreviewPlaceholder.Visibility = Visibility.Visible;
        ManualPreviewStatusTextBlock.Text = "Выберите файл из очереди";
        ManualPreviewFileNameTextBlock.Text = "Файл не выбран";
        ManualPreviewPathTextBlock.Text = string.Empty;
    }

    private void AdminRoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRole == UserRole.Administrator)
        {
            return;
        }

        if (_adminUnlocked)
        {
            _currentRole = UserRole.Administrator;
            SaveCurrentRole();
            UpdateRoleUi();
            StatusTextBlock.Text = "Включена роль администратора.";
            return;
        }

        ShowAdminPasswordPrompt();
    }

    private void DesignerRoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsScreen.Visibility == Visibility.Visible && !ConfirmSettingsNavigation())
        {
            return;
        }

        _currentRole = UserRole.Designer;
        SaveCurrentRole();
        UpdateRoleUi();

        if (SettingsScreen.Visibility == Visibility.Visible)
        {
            ShowScreen(AppScreen.Dashboard);
        }

        StatusTextBlock.Text = "Включена роль дизайнера.";
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRole != UserRole.Administrator)
        {
            StatusTextBlock.Text = "Настройки доступны только администратору.";
            return;
        }

        ShowScreen(AppScreen.Settings);
    }

    private void JournalNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(AppScreen.Journal);
    }

    private void PlaygroundNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(AppScreen.Playground);
    }

    private void LogFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        UpdateSearchResults(_logs.ToList(), LogFilterTextBox.Text.Trim());
    }

    private void LogFilterTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !string.IsNullOrWhiteSpace(LogFilterTextBox.Text))
        {
            UpdateSearchResults(_logs.ToList(), LogFilterTextBox.Text.Trim(), rememberSearch: true);
        }
    }

    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(LogFilterTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateSearchResults(IReadOnlyList<ProcessingLogRow> rows, string filter, bool rememberSearch = false)
    {
        _searchResults.Clear();
        _currentSearchFilter = filter;

        if (string.IsNullOrWhiteSpace(filter))
        {
            _selectedSearchResult = null;
            if (SearchScreen.Visibility == Visibility.Visible)
            {
                ShowScreen(AppScreen.Dashboard);
            }
            else
            {
                SearchScreen.Visibility = Visibility.Collapsed;
            }

            return;
        }

        ShowScreen(AppScreen.Search);

        foreach (var row in rows.Where(row => row.NormalizedTags.Count > 0 && _logQueryService.MatchesFilter(row, filter)))
        {
            _searchResults.Add(row);
        }

        SearchResultsTitleTextBlock.Text = $"Результаты поиска: {filter}";
        SearchHistoryTextBlock.Text = string.Empty;

        if (_searchResults.Count > 0)
        {
            SearchResultsListBox.SelectedIndex = 0;
        }
        else
        {
            ShowSearchResultDetails(null);
        }
    }

    private void SearchResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSearchResultDetails(SearchResultsListBox.SelectedItem as ProcessingLogRow);
    }

    private void ShowSearchResultDetails(ProcessingLogRow? row)
    {
        _selectedSearchResult = row;
        SearchPreviewImage.Source = null;

        if (row is null)
        {
            SearchSelectedFileNameTextBlock.Text = "Ничего не найдено";
            SearchSelectedPathTextBlock.Text = "Поиск показывает только файлы, у которых уже есть теги. Попробуйте другой запрос или проверьте журнал.";
            SearchSelectedResultTextBlock.Text = string.Empty;
            SearchSelectedDateTextBlock.Text = string.Empty;
            SearchSelectedOrderTextBlock.Text = string.Empty;
            SearchSelectedMessageTextBlock.Text = string.Empty;
            SearchSelectedTagsTextBlock.Text = "Нет тегов";
            SearchSelectedTagsPanel.Children.Clear();
            SearchPreviewPlaceholder.Visibility = Visibility.Visible;
            OpenSearchResultFolderButton.Visibility = Visibility.Collapsed;
            return;
        }

        SetHighlightedText(SearchSelectedFileNameTextBlock, row.FileName, _currentSearchFilter, bold: true);
        SetHighlightedText(SearchSelectedPathTextBlock, row.FilePath, _currentSearchFilter);
        SearchSelectedResultTextBlock.Text = row.Result;
        SearchSelectedDateTextBlock.Text = row.OperationTimeDisplay;
        SearchSelectedOrderTextBlock.Text = string.IsNullOrWhiteSpace(row.OrderId) ? "не указан" : row.OrderId;
        SetHighlightedText(SearchSelectedMessageTextBlock, FormatSearchResultMessage(row.Message), _currentSearchFilter);
        var tags = GetTags(row);
        SearchSelectedTagsTextBlock.Text = BuildTagsText(tags);
        RenderSearchTags(tags, _currentSearchFilter);
        OpenSearchResultFolderButton.Visibility = Visibility.Visible;
        TryLoadSearchPreview(row.FilePath);
    }

    private void RenderSearchTags(IReadOnlyCollection<TagRow> tags, string filter)
    {
        SearchSelectedTagsPanel.Children.Clear();
        if (tags.Count == 0)
        {
            SearchSelectedTagsPanel.Children.Add(new TextBlock
            {
                Text = "Нет тегов",
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
            });
            return;
        }

        foreach (var tag in tags)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 8, 8),
                MaxWidth = 360,
            };

            var panel = new StackPanel();
            var key = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 42, 0)),
                TextWrapping = TextWrapping.Wrap,
            };
            SetHighlightedText(key, DisplayTagKey(tag.Key), filter, bold: true);

            var value = new TextBlock
            {
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("Text"),
            };
            SetHighlightedText(value, tag.Value, filter);

            panel.Children.Add(key);
            panel.Children.Add(value);
            border.Child = panel;
            SearchSelectedTagsPanel.Children.Add(border);
        }
    }

    private IReadOnlyCollection<TagRow> GetTags(ProcessingLogRow row)
    {
        return row.NormalizedTags.Count > 0
            ? row.NormalizedTags
            : _logQueryService.ParseTags(row.TagsJson);
    }

    private static string FormatSearchResultMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Сообщение отсутствует.";
        }

        return message
            .Replace("; ", ";" + Environment.NewLine, StringComparison.Ordinal)
            .Replace(". Рекомендуемая", "." + Environment.NewLine + "Рекомендуемая", StringComparison.Ordinal)
            .Replace(". Файл", "." + Environment.NewLine + "Файл", StringComparison.Ordinal);
    }

    private static string DisplayTagKey(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "description" or "visual_description" => "Описание",
            "visible_text" => "Надписи",
            "dominant_colors" or "colors" or "color" => "Цвета",
            "background" => "Фон",
            "composition" => "Композиция",
            "object_type" => "Тип макета",
            "product_type" or "product" => "Продукт",
            "style" => "Стиль",
            "mood" => "Настроение",
            "purpose" => "Назначение",
            "audience" => "Аудитория",
            "format" => "Формат",
            "client" => "Клиент",
            "order_id" or "orderid" => "Заказ",
            "file_name" => "Файл",
            "extension" => "Расширение",
            "search_keywords" => "Ключевые слова",
            var value when value.StartsWith("search_keyword_", StringComparison.Ordinal) => "Ключевое слово",
            _ => key.Replace("_", " "),
        };
    }

    private void SetHighlightedText(TextBlock textBlock, string text, string filter, bool bold = false)
    {
        textBlock.Inlines.Clear();
        textBlock.Text = string.Empty;

        if (bold)
        {
            textBlock.FontWeight = FontWeights.SemiBold;
        }

        var tokens = GetSearchTokens(filter)
            .Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static token => token.Length)
            .ToList();

        if (tokens.Count == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var index = 0;
        while (index < text.Length)
        {
            var match = tokens
                .Select(token => new
                {
                    Token = token,
                    Index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase),
                })
                .Where(match => match.Index >= 0)
                .OrderBy(match => match.Index)
                .ThenByDescending(match => match.Token.Length)
                .FirstOrDefault();

            if (match is null)
            {
                textBlock.Inlines.Add(new Run(text[index..]));
                break;
            }

            if (match.Index > index)
            {
                textBlock.Inlines.Add(new Run(text[index..match.Index]));
            }

            textBlock.Inlines.Add(new Run(text.Substring(match.Index, match.Token.Length))
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 240, 138)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                FontWeight = FontWeights.SemiBold,
            });

            index = match.Index + match.Token.Length;
        }
    }

    private static IReadOnlyCollection<string> GetSearchTokens(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return Array.Empty<string>();
        }

        return filter
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void TryLoadSearchPreview(string filePath)
    {
        if (!File.Exists(filePath))
        {
            SearchPreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(filePath);
            image.EndInit();
            image.Freeze();

            SearchPreviewImage.Source = image;
            SearchPreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            SearchPreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void OpenSearchResultFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchResult is not null)
        {
            OpenFileLocation(_selectedSearchResult.FilePath);
        }
    }

    private void CloseSearchResultsButton_Click(object sender, RoutedEventArgs e)
    {
        LogFilterTextBox.Clear();
        ShowScreen(AppScreen.Dashboard);
    }

    private void RememberSearch(string filter)
    {
        var normalized = filter.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var existing = _searchHistory.FirstOrDefault(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _searchHistory.Remove(existing);
        }

        _searchHistory.Insert(0, normalized);
        while (_searchHistory.Count > 8)
        {
            _searchHistory.RemoveAt(_searchHistory.Count - 1);
        }

        SaveSearchHistory();
    }

    private void LoadSearchHistory()
    {
        try
        {
            var path = GetSearchHistoryPath();
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(path).Where(static x => !string.IsNullOrWhiteSpace(x)).Take(8))
            {
                _searchHistory.Add(line.Trim());
            }
        }
        catch
        {
        }
    }

    private void SaveSearchHistory()
    {
        try
        {
            var path = GetSearchHistoryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, _searchHistory);
        }
        catch
        {
        }
    }

    private static string GetSearchHistoryPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaModule.Desktop");

        return Path.Combine(directory, "search-history.txt");
    }

    private void ShowAdminPasswordPrompt()
    {
        AdminPasswordErrorTextBlock.Visibility = Visibility.Collapsed;
        AdminPasswordBox.Password = string.Empty;
        AdminPasswordOverlay.Visibility = Visibility.Visible;
        AdminPasswordBox.Focus();
    }

    private void SubmitAdminPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        TrySwitchToAdmin();
    }

    private void CancelAdminPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        HideAdminPasswordPrompt();
    }

    private void AdminPasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TrySwitchToAdmin();
        }
    }

    private void AdminPasswordOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == AdminPasswordOverlay)
        {
            HideAdminPasswordPrompt();
        }
    }

    private void AdminPasswordDialog_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void TrySwitchToAdmin()
    {
        if (AdminPasswordBox.Password != AdminPassword)
        {
            AdminPasswordErrorTextBlock.Visibility = Visibility.Visible;
            AdminPasswordBox.SelectAll();
            AdminPasswordBox.Focus();
            return;
        }

        _currentRole = UserRole.Administrator;
        _adminUnlocked = true;
        SaveCurrentRole();
        HideAdminPasswordPrompt();
        UpdateRoleUi();
        StatusTextBlock.Text = "Включена роль администратора.";
    }

    private void HideAdminPasswordPrompt()
    {
        AdminPasswordOverlay.Visibility = Visibility.Collapsed;
        AdminPasswordBox.Password = string.Empty;
        AdminPasswordErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void UpdateRoleUi()
    {
        var activeBrush = (System.Windows.Media.Brush)FindResource("Accent");
        var mutedBrush = (System.Windows.Media.Brush)FindResource("Muted");

        AdminRoleButton.Foreground = _currentRole == UserRole.Administrator ? activeBrush : mutedBrush;
        AdminRoleButton.FontWeight = _currentRole == UserRole.Administrator ? FontWeights.SemiBold : FontWeights.Normal;
        DesignerRoleButton.Foreground = _currentRole == UserRole.Designer ? activeBrush : mutedBrush;
        DesignerRoleButton.FontWeight = _currentRole == UserRole.Designer ? FontWeights.SemiBold : FontWeights.Normal;

        SettingsNavButton.Visibility = _currentRole == UserRole.Administrator
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void LoadSavedRole()
    {
        try
        {
            var path = GetRoleStatePath();
            if (!File.Exists(path))
            {
                return;
            }

            var savedRole = File.ReadAllText(path).Trim();
            _currentRole = string.Equals(savedRole, nameof(UserRole.Designer), StringComparison.OrdinalIgnoreCase)
                ? UserRole.Designer
                : UserRole.Administrator;
            _adminUnlocked = _currentRole == UserRole.Administrator;
        }
        catch
        {
            _currentRole = UserRole.Administrator;
            _adminUnlocked = true;
        }
    }

    private void SaveCurrentRole()
    {
        try
        {
            var path = GetRoleStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _currentRole.ToString());
        }
        catch
        {
            // Role persistence is a convenience; the UI can continue without it.
        }
    }

    private static string GetRoleStatePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaModule.Desktop");

        return Path.Combine(directory, "last-role.txt");
    }

    private void ShowScreen(AppScreen screen)
    {
        if (SettingsScreen.Visibility == Visibility.Visible &&
            screen != AppScreen.Settings &&
            !ConfirmSettingsNavigation())
        {
            return;
        }

        if (screen == AppScreen.Settings && _currentRole != UserRole.Administrator)
        {
            screen = AppScreen.Dashboard;
        }

        DashboardScreen.Visibility = screen == AppScreen.Dashboard ? Visibility.Visible : Visibility.Collapsed;
        ManualCheckScreen.Visibility = screen == AppScreen.ManualCheck ? Visibility.Visible : Visibility.Collapsed;
        SettingsScreen.Visibility = screen == AppScreen.Settings ? Visibility.Visible : Visibility.Collapsed;
        JournalScreen.Visibility = screen == AppScreen.Journal ? Visibility.Visible : Visibility.Collapsed;
        PlaygroundScreen.Visibility = screen == AppScreen.Playground ? Visibility.Visible : Visibility.Collapsed;
        SearchScreen.Visibility = screen == AppScreen.Search ? Visibility.Visible : Visibility.Collapsed;

        BreadcrumbPanel.Visibility = screen == AppScreen.Dashboard ? Visibility.Collapsed : Visibility.Visible;
        BreadcrumbHomeButton.IsEnabled = screen != AppScreen.Dashboard;
        BreadcrumbHomeButton.Foreground = screen == AppScreen.Dashboard
            ? (System.Windows.Media.Brush)FindResource("Muted")
            : (System.Windows.Media.Brush)FindResource("Accent");

        BreadcrumbCurrentTextBlock.Text = screen switch
        {
            AppScreen.Dashboard => "Дашборд",
            AppScreen.ManualCheck => "Проверка файлов",
            AppScreen.Settings => "Настройки",
            AppScreen.Journal => "Журнал операций",
            AppScreen.Playground => "GigaChat Playground",
            AppScreen.Search => "Результаты поиска",
            _ => string.Empty,
        };

        HeaderTitleTextBlock.Text = screen switch
        {
            AppScreen.Dashboard => "Контроль обработки графических файлов",
            AppScreen.ManualCheck => "Проверка архивных файлов",
            AppScreen.Settings => "Настройки системы",
            AppScreen.Journal => "Журнал обработки",
            AppScreen.Playground => "Тестирование тегирования",
            AppScreen.Search => "Подобранные файлы",
            _ => "MediaModule",
        };

        SetNavButtonState(DashboardNavButton, screen == AppScreen.Dashboard);
        SetNavButtonState(ManualCheckNavButton, screen == AppScreen.ManualCheck);
        SetNavButtonState(SettingsNavButton, screen == AppScreen.Settings);
        SetNavButtonState(JournalNavButton, screen == AppScreen.Journal);
        SetNavButtonState(PlaygroundNavButton, screen == AppScreen.Playground);
    }

    private bool ConfirmSettingsNavigation()
    {
        if (!_settingsDirty)
        {
            return true;
        }

        var result = System.Windows.MessageBox.Show(
            "Настройки были изменены. Сохранить изменения?",
            "Несохраненные настройки",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                SaveCurrentSettings("Настройки сохранены.");
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Ошибка сохранения: {ex.Message}";
                return false;
            }
        }
        else
        {
            LoadSettingsIntoUi();
            StatusTextBlock.Text = "Изменения настроек отменены.";
        }

        return true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (SettingsScreen.Visibility == Visibility.Visible && !ConfirmSettingsNavigation())
        {
            e.Cancel = true;
        }
    }

    private void SetNavButtonState(WpfButton button, bool active)
    {
        button.Background = active
            ? (System.Windows.Media.Brush)FindResource("Active")
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = active
            ? (System.Windows.Media.Brush)FindResource("Accent")
            : (System.Windows.Media.Brush)FindResource("Text");
    }

    private static string FindWorkerSettingsPath()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var current = baseDir;

        while (current is not null)
        {
            var slnPath = Path.Combine(current.FullName, "MediaModule.sln");
            if (File.Exists(slnPath))
            {
                return Path.Combine(current.FullName, "src", "MediaModule.Worker", "appsettings.json");
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MediaModule.Worker", "appsettings.json"));
    }
}

