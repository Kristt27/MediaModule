using MediaModule.Domain.Entities;

namespace MediaModule.Application.Configuration;

public sealed class ModuleOptions
{
    public string RootDirectory { get; set; } = "D:\\Design";

    public string FileNameRegexPattern { get; set; } = "^[\\p{L}0-9]+_[\\p{L}0-9]+_20\\d{2}_\\d+\\.[A-Za-z0-9]+$";

    public bool ValidateFileName { get; set; } = true;

    public bool ValidatePath { get; set; } = true;

    public bool DetectDuplicates { get; set; } = true;

    public int DuplicateHashDistanceThreshold { get; set; } = 5;

    public List<string> MonitoredDirectories { get; set; } = new();

    public bool IncludeSubdirectories { get; set; } = true;

    public int EventDebounceMilliseconds { get; set; } = 1200;

    public List<string> IgnoredDirectories { get; set; } = new();

    public List<string> AllowedExtensions { get; set; } =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
        ".tif",
        ".tiff",
    ];

    public bool RollbackBlockedFiles { get; set; } = true;

    public string RejectedFilesDirectory { get; set; } = "rejected";

    public bool AutoAcceptTags { get; set; }

    public string DatabasePath { get; set; } = "data/module.db";

    public ElmaMockOptions ElmaMock { get; set; } = new();

    public MiniCrmOptions MiniCrm { get; set; } = new();

    public GigaChatOptions GigaChat { get; set; } = new();
}

public sealed class ElmaMockOptions
{
    public List<OrderData> Orders { get; set; } = new();
}

public sealed class MiniCrmOptions
{
    public List<OrderData> Orders { get; set; } = new();
}

public sealed class GigaChatOptions
{
    public bool Enabled { get; set; }

    public bool UseMockFallback { get; set; } = true;

    public string AuthorizationKey { get; set; } = string.Empty;

    public string Scope { get; set; } = "GIGACHAT_API_PERS";

    public string OAuthUrl { get; set; } = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";

    public string ApiBaseUrl { get; set; } = "https://gigachat.devices.sberbank.ru/api/v1";

    public string Model { get; set; } = "GigaChat-Pro";

    public int TimeoutSeconds { get; set; } = 30;

    public bool IgnoreSslCertificateErrors { get; set; }
}
