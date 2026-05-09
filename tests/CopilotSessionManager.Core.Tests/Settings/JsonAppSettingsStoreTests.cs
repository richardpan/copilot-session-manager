using System;
using System.IO;
using System.Text.Json;
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

    private JsonAppSettingsStore SutWith(params IAppSettingsMigration[] migrations)
        => new(_path, NullLogger<JsonAppSettingsStore>.Instance, migrations);

    [Fact]
    public async Task Load_NoFile_ReturnsDefaults()
    {
        var settings = await Sut().LoadAsync();
        settings.OnboardingCompleted.Should().BeFalse();
        settings.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
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

    [Fact]
    public void Constructor_RejectsNullMigrations()
    {
        FluentActions.Invoking(() => new JsonAppSettingsStore(
            _path, NullLogger<JsonAppSettingsStore>.Instance, migrations: null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Save_StampsCurrentSchemaVersion_OnDisk()
    {
        await Sut().SaveAsync(new AppSettings { OnboardingCompleted = true });

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        doc.RootElement.GetProperty("schemaVersion").GetInt32()
            .Should().Be(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Load_LegacyFileMissingSchemaVersion_StampsCurrentVersionAndCreatesBackup()
    {
        // Pre-versioning legacy shape: schemaVersion key absent => loader treats as v0.
        await File.WriteAllTextAsync(_path, "{ \"onboardingCompleted\": true, \"logLevel\": \"Debug\" }");

        var loaded = await Sut().LoadAsync();

        loaded.OnboardingCompleted.Should().BeTrue();
        loaded.LogLevel.Should().Be("Debug");
        loaded.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);

        var backup = _path + ".bak.0";
        File.Exists(backup).Should().BeTrue("a backup of the pre-migration file should exist");
        (await File.ReadAllTextAsync(backup)).Should().NotContain("schemaVersion");

        // The on-disk file should now carry the current schema version.
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        doc.RootElement.GetProperty("schemaVersion").GetInt32()
            .Should().Be(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Load_FileAlreadyAtCurrentVersion_DoesNotCreateBackup()
    {
        await Sut().SaveAsync(new AppSettings { OnboardingCompleted = true });

        await Sut().LoadAsync();

        Directory.GetFiles(_dir, "settings.json.bak.*").Should().BeEmpty();
    }

    [Fact]
    public async Task Load_RegisteredMigration_RunsAndAdvancesVersion()
    {
        // Hand-roll a v0 file with an extra "legacy" field a future migration would consume.
        await File.WriteAllTextAsync(_path,
            "{ \"onboardingCompleted\": false, \"legacyFlag\": \"yes\" }");

        var migration = new RecordingMigration(fromVersion: 0, sideEffect: s => s.OnboardingCompleted = true);
        var loaded = await SutWith(migration).LoadAsync();

        migration.Invocations.Should().Be(1);
        loaded.OnboardingCompleted.Should().BeTrue();
        loaded.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Load_FailingMigration_RestoresBackupAndReturnsDefaults()
    {
        var originalJson = "{ \"onboardingCompleted\": true, \"logLevel\": \"Debug\" }";
        await File.WriteAllTextAsync(_path, originalJson);

        var bomb = new RecordingMigration(
            fromVersion: 0,
            sideEffect: _ => throw new InvalidOperationException("nope"));
        var loaded = await SutWith(bomb).LoadAsync();

        loaded.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        loaded.OnboardingCompleted.Should().BeFalse("rollback returns Defaults on failure");

        // The on-disk file should be restored to the pre-migration content.
        (await File.ReadAllTextAsync(_path)).Should().Be(originalJson);
    }

    [Fact]
    public async Task Reset_DeletesFileAndCreatesResetBackup()
    {
        var sut = Sut();
        await sut.SaveAsync(new AppSettings { OnboardingCompleted = true });

        await sut.ResetAsync();

        File.Exists(_path).Should().BeFalse();
        File.Exists(_path + ".reset.bak").Should().BeTrue();

        var afterReset = await sut.LoadAsync();
        afterReset.OnboardingCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Reset_NoFile_IsNoOp()
    {
        await Sut().ResetAsync();
        File.Exists(_path).Should().BeFalse();
        File.Exists(_path + ".reset.bak").Should().BeFalse();
    }

    private sealed class RecordingMigration : IAppSettingsMigration
    {
        private readonly Action<AppSettings> _sideEffect;

        public RecordingMigration(int fromVersion, Action<AppSettings> sideEffect)
        {
            FromVersion = fromVersion;
            _sideEffect = sideEffect;
        }

        public int FromVersion { get; }

        public int Invocations { get; private set; }

        public void Apply(AppSettings settings)
        {
            Invocations++;
            _sideEffect(settings);
        }
    }
}
