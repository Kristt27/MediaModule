using System.Runtime.Versioning;
using MediaModule.Application.Abstractions;
using MediaModule.Infrastructure.Integration;
using MediaModule.Infrastructure.Monitoring;
using MediaModule.Infrastructure.Persistence;
using MediaModule.Infrastructure.Services;
using MediaModule.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace MediaModule.Infrastructure;

public static class DependencyInjection
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddMediaModuleInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IFileEventSource, FileSystemWatcherEventSource>();
        services.AddSingleton<IFileRuleValidator, RegexFileRuleValidator>();
        services.AddSingleton<IElmaClient, RealElmaClient>();
        services.AddSingleton<IGigaChatClient, RealGigaChatClient>();
        services.AddSingleton<IDuplicateDetector, AverageHashDuplicateDetector>();
        services.AddSingleton<IDuplicateResolutionService, WindowsDuplicateResolutionService>();
        services.AddSingleton<IModuleRepository, SqliteModuleRepository>();
        services.AddSingleton<IViolationPolicy, InMemoryViolationPolicy>();
        services.AddSingleton<IOrderSelectionService, WindowsOrderSelectionService>();
        services.AddSingleton<IFileCorrectionService, WindowsFileCorrectionService>();
        services.AddSingleton<ITagReviewService, WindowsTagReviewService>();
        services.AddSingleton<IFileNotificationService, WindowsTrayNotificationService>();

        return services;
    }
}
