using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Settings;

public class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonAppSettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private JsonAppSettingsStore Sut() => new(_path, NullLogger<JsonAppSettingsStore>.Instance);

    [Fact]
    public async Task Load_NoFile_ReturnsDefaults()
    {
        var settings = await Sut().LoadAsync();
        settings.OnboardingCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        var sut = Sut();
        await sut.SaveAsync(new AppSettings { OnboardingCompleted = true });

        var loaded = await sut.LoadAsync();
        loaded.OnboardingCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Save_CreatesParentDirectory()
    {
        var nestedPath = Path.Combine(_dir, "deeper", "nested", "settings.json");
        var sut = new JsonAppSettingsStore(nestedPath, NullLogger<JsonAppSettingsStore>.Instance);
        await sut.SaveAsync(new AppSettings { OnboardingCompleted = true });

        File.Exists(nestedPath).Should().BeTrue();
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaults()
    {
        await File.WriteAllTextAsync(_path, "{not valid json");
        var settings = await Sut().LoadAsync();
        settings.OnboardingCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Save_OverwritesExistingFile()
    {
        var sut = Sut();
        await sut.SaveAsync(new AppSettings { OnboardingCompleted = true });
        await sut.SaveAsync(new AppSettings { OnboardingCompleted = false });
        (await sut.LoadAsync()).OnboardingCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Save_PersistsAtomically_NoTempFileLeftBehind()
    {
        await Sut().SaveAsync(new AppSettings { OnboardingCompleted = true });
        File.Exists(_path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Constructor_RejectsBlankPath()
    {
        FluentActions.Invoking(() => new JsonAppSettingsStore("", NullLogger<JsonAppSettingsStore>.Instance))
            .Should().Throw<ArgumentException>();
    }
}
