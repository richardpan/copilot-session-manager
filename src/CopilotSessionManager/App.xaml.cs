using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CopilotSessionManager.Core.Configuration;
using CopilotSessionManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CopilotSessionManager;

/// <summary>
/// Application entry point. Owns the generic-host lifecycle and the DI container.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>
    /// Gets the application's <see cref="IServiceProvider"/>. Available after
    /// <see cref="OnStartup"/> has run.
    /// </summary>
    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
            ?? throw new InvalidOperationException("Host has not started.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(ConfigureServices)
                .Build();

            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            Log.Information(
                "{Product} {Version} started.",
                AppMetadata.ProductName,
                AppMetadata.Version);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during startup.");
            MessageBox.Show(
                $"The application could not start.\n\n{ex.Message}\n\nSee logs at {logDirectory}",
                "Copilot Session Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down.");

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Views
        services.AddSingleton<MainWindow>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();

        // Core services will be registered here as they're built out.
    }
}
