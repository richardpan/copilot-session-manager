using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.GitHub.Storage;

/// <summary>
/// File-backed <see cref="ISessionGitHubLinksStore"/>. Writes one small JSON
/// document per session at
/// <c>&lt;sessionFolder&gt;/github-overrides.json</c> using the
/// write-temp-then-rename atomic pattern shared with
/// <see cref="JsonSessionLabelStore"/> and
/// <see cref="Settings.JsonAppSettingsStore"/>.
/// </summary>
public sealed class JsonSessionGitHubLinksStore : ISessionGitHubLinksStore
{
    /// <summary>The on-disk file name written into each session folder.</summary>
    public const string FileName = "github-overrides.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISessionFolderReader _folders;
    private readonly ILogger<JsonSessionGitHubLinksStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSessionGitHubLinksStore(
        ISessionFolderReader folders,
        ILogger<JsonSessionGitHubLinksStore> logger)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);
        _folders = folders;
        _logger = logger;
    }

    /// <summary>The path the store resolves for <paramref name="sessionId"/>.</summary>
    public string GetOverridesPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(_folders.GetSessionFolderPath(sessionId), FileName);
    }

    public async Task<SessionGitHubLinkOverrides?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var path = GetOverridesPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            var doc = await JsonSerializer
                .DeserializeAsync<OverridesDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (doc is null)
            {
                return null;
            }

            var overrides = new SessionGitHubLinkOverrides(
                NullIfBlank(doc.Repository),
                NullIfBlank(doc.Branch),
                doc.PullRequestNumber);

            return overrides.HasAnyOverride ? overrides : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not read GitHub overrides at {Path}; treating as no overrides.",
                path);
            TryBackupCorruptFile(path);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(
        string sessionId,
        SessionGitHubLinkOverrides overrides,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(overrides);

        // Empty overrides ↔ no file. Keeps the on-disk surface tidy and means
        // GetAsync will return null instead of an Empty record.
        if (!overrides.HasAnyOverride)
        {
            await ClearAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = GetOverridesPath(sessionId);
        var folder = Path.GetDirectoryName(path)!;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);

            var doc = new OverridesDocument
            {
                Version = 1,
                Repository = overrides.RepositoryOverride,
                Branch = overrides.BranchOverride,
                PullRequestNumber = overrides.PullRequestNumberOverride,
            };

            var temp = path + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, useAsync: true))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, doc, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var path = GetOverridesPath(sessionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not delete GitHub overrides at {Path}; leaving in place.",
                    path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; nothing else to do.
        }
    }

    private void TryBackupCorruptFile(string path)
    {
        try
        {
            var backup = path + ".bak." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(path, backup);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not back up corrupt overrides file at {Path}.", path);
        }
    }

    private sealed class OverridesDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("repository")]
        public string? Repository { get; set; }

        [JsonPropertyName("branch")]
        public string? Branch { get; set; }

        [JsonPropertyName("pullRequestNumber")]
        public int? PullRequestNumber { get; set; }
    }
}
