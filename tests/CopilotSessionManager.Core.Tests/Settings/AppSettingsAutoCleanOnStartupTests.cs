using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Settings;

/// <summary>
/// V1.8 (#74) tests for the new <see cref="AppSettings.AutoCleanStaleLocksOnStartup"/>
/// flag. These guard the additive non-breaking shape: a fresh install
/// preserves the historical opt-in behaviour, and the flag round-trips
/// cleanly through JSON.
/// </summary>
public class AppSettingsAutoCleanOnStartupTests
{
    [Fact]
    public void Defaults_AutoCleanStaleLocksOnStartup_IsFalse()
    {
        var settings = AppSettings.Defaults();
        settings.AutoCleanStaleLocksOnStartup.Should().BeFalse(
            "the historical no-op behaviour must be preserved on first launch — opt-in only");
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAutoCleanFlag_True()
    {
        var dir = Path.Combine(Path.GetTempPath(), "csm-autoclean-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var sut = new JsonAppSettingsStore(path, NullLogger<JsonAppSettingsStore>.Instance);
            await sut.SaveAsync(new AppSettings { AutoCleanStaleLocksOnStartup = true });

            var loaded = await sut.LoadAsync();
            loaded.AutoCleanStaleLocksOnStartup.Should().BeTrue();
        }
        finally
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task LoadingPreV18File_DefaultsAutoCleanToFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "csm-autoclean-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            // Simulates a settings.json saved by a pre-V1.8 build that has no
            // AutoCleanStaleLocksOnStartup property at all. The loader must
            // default it to false so historic users keep their no-op startup.
            await File.WriteAllTextAsync(path,
                "{\"schemaVersion\":1,\"onboardingCompleted\":true,\"logLevel\":\"Information\",\"minimizeToTrayOnClose\":true}");

            var sut = new JsonAppSettingsStore(path, NullLogger<JsonAppSettingsStore>.Instance);
            var loaded = await sut.LoadAsync();

            loaded.OnboardingCompleted.Should().BeTrue("pre-existing settings must still load");
            loaded.AutoCleanStaleLocksOnStartup.Should().BeFalse(
                "missing-from-disk must default to the safe opt-in value");
        }
        finally
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
