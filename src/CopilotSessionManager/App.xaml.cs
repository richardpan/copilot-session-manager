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
using CopilotSessionManager.Services.SingleInstance;
using CopilotSessionManager.Services.Tray;
using CopilotSessionManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private MutexSingleInstanceCoordinator? _singleInstance;
    private ITrayIconService? _trayIcon;
    private TrayCoordinator? _trayCoordinator;
    private bool _userRequestedQuit;

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

        // Single-instance gate runs before anything expensive. If another
        // instance is already running we ping it (it raises ActivationRequested)
        // and exit silently. The gate uses a per-user mutex + named pipe.
        _singleInstance = new MutexSingleInstanceCoordinator(NullLogger<MutexSingleInstanceCoordinator>.Instance);
        var acquired = await _singleInstance.TryAcquireAsync().ConfigureAwait(true);
        if (!acquired)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(exitCode: 0);
            return;
        }
        _singleInstance.ActivationRequested += OnActivationRequested;

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
                var bootstrapLogger = NullLogger<JsonAppSettingsStore>.Instance;
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
            mainWindow.Closing += OnMainWindowClosing;
            mainWindow.Show();

            // Tray icon goes up only after the main window is on screen, so
            // a tooltip update never beats the first session scan to the
            // dispatcher.
            try
            {
                _trayIcon = _host.Services.GetRequiredService<ITrayIconService>();
                var sessions = _host.Services.GetRequiredService<SessionsViewModel>();
                _trayCoordinator = new TrayCoordinator(
                    _trayIcon,
                    sessions.Sessions,
                    onActivate: ActivateMainWindow,
                    onQuit: RequestQuit);
                _trayIcon.Show();
            }
            catch (Exception tex)
            {
                // Tray is non-essential — don't take down the app if it
                // fails to materialise (e.g. headless / RDP edge cases).
                Log.Warning(tex, "Tray icon failed to initialise; continuing without one.");
            }

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

        _trayCoordinator?.Dispose();
        _trayCoordinator = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= OnActivationRequested;
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    /// <summary>
    /// Raised by <see cref="MutexSingleInstanceCoordinator"/> when a second
    /// process tried to launch. Marshal to the UI thread, then restore +
    /// foreground the existing main window.
    /// </summary>
    private void OnActivationRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(ActivateMainWindow));
    }

    /// <summary>
    /// Show + foreground the main window. Idempotent. Used by both the
    /// single-instance pipeline and the tray coordinator.
    /// </summary>
    private void ActivateMainWindow()
    {
        Dispatcher.VerifyAccess();

        var window = MainWindow;
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Show();
        window.Activate();

        // Topmost flicker is the standard WPF trick for forcing a window
        // back to the foreground when SetForegroundWindow would otherwise
        // be denied by Win32 focus rules.
        var wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
    }

    /// <summary>
    /// User picked "Quit" from the tray context menu (or any future
    /// affordance). Marks the intent to actually exit so the next close
    /// of the main window doesn't bounce back into the tray, then closes
    /// the window which triggers <see cref="OnMainWindowClosing"/> →
    /// <see cref="Application.Shutdown()"/>.
    /// </summary>
    private void RequestQuit()
    {
        _userRequestedQuit = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow is not null)
            {
                MainWindow.Close();
            }
            else
            {
                Shutdown(0);
            }
        }));
    }

    /// <summary>
    /// Intercepts the main-window close button. By default we honour
    /// <see cref="AppSettings.MinimizeToTrayOnClose"/> and just hide the
    /// window — the process keeps running in the tray and can be brought
    /// back via the icon. An explicit Quit (tray menu) bypasses this by
    /// flipping <see cref="_userRequestedQuit"/> first.
    /// </summary>
    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_userRequestedQuit || _trayIcon is null || _host is null)
        {
            return;
        }

        try
        {
            var store = _host.Services.GetRequiredService<IAppSettingsStore>();
            var settings = await store.LoadAsync().ConfigureAwait(true);
            if (settings.MinimizeToTrayOnClose && sender is Window window)
            {
                e.Cancel = true;
                window.Hide();
            }
        }
        catch (Exception ex)
        {
            // Failing to read the setting must not block the user from
            // closing the window — fall through to the default close.
            Log.Warning(ex, "Could not consult MinimizeToTrayOnClose setting; closing normally.");
        }
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

        // Tray icon is Windows-only and the WPF host is itself Windows-only
        // (TFM net8.0-windows), so registering the concrete service here is
        // safe. Register as singleton so the icon's lifetime tracks the host.
        services.AddSingleton<ITrayIconService, NotifyIconTrayService>();

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
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
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
