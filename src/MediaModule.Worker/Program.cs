using System.Threading;
using System.Windows.Forms;
using MediaModule.Application.Configuration;
using MediaModule.Application.Services;
using MediaModule.Infrastructure;
using MediaModule.Worker;
using Serilog;

using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\MediaModule.Worker", createdNew: out var isFirstInstance);
if (!isFirstInstance)
{
    MessageBox.Show(
        text: "MediaModule Worker уже запущен.",
        caption: "MediaModule",
        buttons: MessageBoxButtons.OK,
        icon: MessageBoxIcon.Information);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ModuleOptions>(builder.Configuration.GetSection("Module"));
builder.Services.AddMediaModuleInfrastructure();
builder.Services.AddSingleton<FileProcessingOrchestrator>();
builder.Services.AddHostedService<Worker>();

builder.Services.AddSerilog((services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

var host = builder.Build();
host.Run();
