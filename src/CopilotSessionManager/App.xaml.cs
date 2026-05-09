using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CopilotSessionManager.Core.Configuration;
using CopilotSessionManager.Core.DependencyInjection;
using CopilotSessionManager.Core.Onboarding;
using CopilotSessionManager.Core.Settings;
using CopilotSessionManager.Logging;
using CopilotSessionManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CopilotSessionManager;

/// <summary>
/// Application entry point. Owns the generic-host lifecycle and the DI container.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Information);

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

        // Probe the Copilot CLI version once at startup so every log line gets
        // it via the BuildInfoEnricher. Best-effort: if the probe fails we
        // record "unknown" and continue.
        var copilotCliVersion = await ProbeCopilotCliVersionAsync().ConfigureAwait(true);

        // Pre-load persisted log level (default Information).
        try
        {
            var settingsPath = Path.Combine(AppPaths.LocalAppDataDirectory, JsonAppSettingsStore.DefaultFileName);
            if (File.Exists(settingsPath))
            {
                var bootstrapLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonAppSettingsStore>.Instance;
                var bootstrapStore = new JsonAppSettingsStore(settingsPath, bootstrapLogger);
                var settings = await bootstrapStore.LoadAsync().ConfigureAwait(true);
                _levelSwitch.MinimumLevel = LogLevelSwitchAccessor.ParseLevel(settings.LogLevel);
            }
        }
        catch
        {
            // Settings are best-effort; default to Information.
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .Enrich.FromLogContext()
            .Enrich.With(new BuildInfoEnricher(copilotCliVersion))
            .Enrich.With(new LogRedactionEnricher())
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] v{AppVersion} cli={CopilotCliVersion} {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(ConfigureServices)
                .Build();

            await _host.StartAsync();

            // First-run onboarding gate: show modally before MainWindow if the
            // user hasn't completed the welcome flow yet. Re-runnable any time
            // from the Help menu.
            var settingsStore = _host.Services.GetRequiredService<IAppSettingsStore>();
            var settings = await settingsStore.LoadAsync();
            if (!settings.OnboardingCompleted)
            {
                try
                {
                    var onboarding = _host.Services.GetRequiredService<OnboardingWindow>();
                    onboarding.Owner = null;
                    onboarding.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    onboarding.ShowDialog();
                }
                catch (Exception oex)
                {
                    Log.Warning(oex, "Onboarding window failed to display; continuing to main window.");
                }
            }

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

    private void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Core
        services.AddSessionDiscovery();
        services.AddOnboarding();
        CoreServiceCollectionExtensions.AddLogging(services);

        // UI infrastructure — the dispatcher must wrap the WPF UI thread.
        services.AddSingleton<IUiDispatcher>(_ => new WpfDispatcher(Current.Dispatcher));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Services.IFileLauncher, Services.ShellFileLauncher>();
        services.AddSingleton(new LogLevelSwitchAccessor(_levelSwitch));

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<OnboardingWindow>();

        // ViewModels
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<OnboardingViewModel>();
    }

    /// <summary>
    /// Run <c>copilot --version</c> with a short timeout so we can stamp the
    /// Copilot CLI version onto every log line. Returns "unknown" if anything
    /// goes wrong so startup never blocks on this probe.
    /// </summary>
    private static async Task<string> ProbeCopilotCliVersionAsync()
    {
        try
        {
            var runner = new ProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRunner>.Instance);
            var result = await runner
                .RunAsync(new ProcessRunRequest("copilot", new[] { "--version" }, TimeoutSeconds: 5))
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return "unknown";
            }
            return result.StdOut.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
