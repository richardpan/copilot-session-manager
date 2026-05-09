using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Issues;
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
/// <remarks>
/// The on-disk schema versions are:
/// <list type="bullet">
///   <item><c>v1</c>: <c>repository</c>, <c>branch</c>, <c>pullRequestNumber</c>.</item>
///   <item><c>v2</c>: adds <c>issueRefs</c> (list of <c>owner/repo#NN</c> strings).</item>
/// </list>
/// v1 documents read cleanly as v2 with an empty <c>issueRefs</c> list, so
/// the upgrade is non-breaking for users coming from a previous build.
/// </remarks>
public sealed class JsonSessionGitHubLinksStore : ISessionGitHubLinksStore
{
    /// <summary>The on-disk file name written into each session folder.</summary>
    public const string FileName = "github-overrides.json";

    /// <summary>Current on-disk schema version.</summary>
    internal const int CurrentVersion = 2;

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
            return await ReadInternalAsync(path, cancellationToken).ConfigureAwait(false);
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteInternalAsync(sessionId, overrides, cancellationToken).ConfigureAwait(false);
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

    public async Task AddIssueRefAsync(
        string sessionId,
        IssueRef issueRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(issueRef);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadOrEmptyAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var canonical = issueRef.ToString();

            if (current.IssueRefs.Any(r => string.Equals(r, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var next = AppendIssueRef(current, canonical);
            await WriteInternalAsync(sessionId, next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveIssueRefAsync(
        string sessionId,
        IssueRef issueRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(issueRef);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadOrEmptyAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var canonical = issueRef.ToString();

            if (current.IssueRefs.Count == 0
                || !current.IssueRefs.Any(r => string.Equals(r, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var filtered = current.IssueRefs
                .Where(r => !string.Equals(r, canonical, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var next = current with { IssueRefs = filtered };
            if (!next.HasAnyOverride)
            {
                var path = GetOverridesPath(sessionId);
                if (File.Exists(path))
                {
                    try
                    { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not delete GitHub overrides at {Path}; leaving in place.", path);
                    }
                }
                return;
            }

            await WriteInternalAsync(sessionId, next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SessionGitHubLinkOverrides AppendIssueRef(SessionGitHubLinkOverrides current, string canonical)
    {
        var list = new List<string>(current.IssueRefs.Count + 1);
        list.AddRange(current.IssueRefs);
        list.Add(canonical);
        return current with { IssueRefs = list };
    }

    private async Task<SessionGitHubLinkOverrides> ReadOrEmptyAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var path = GetOverridesPath(sessionId);
        if (!File.Exists(path))
        {
            return SessionGitHubLinkOverrides.Empty;
        }
        return await ReadInternalAsync(path, cancellationToken).ConfigureAwait(false)
            ?? SessionGitHubLinkOverrides.Empty;
    }

    private async Task<SessionGitHubLinkOverrides?> ReadInternalAsync(
        string path,
        CancellationToken cancellationToken)
    {
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
                doc.PullRequestNumber)
            {
                IssueRefs = NormaliseIssueRefs(doc.IssueRefs),
            };

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
    }

    private static IReadOnlyList<string> NormaliseIssueRefs(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(raw.Count);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            var trimmed = entry.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }
        return result;
    }

    private async Task WriteInternalAsync(
        string sessionId,
        SessionGitHubLinkOverrides overrides,
        CancellationToken cancellationToken)
    {
        var path = GetOverridesPath(sessionId);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);

        var doc = new OverridesDocument
        {
            Version = CurrentVersion,
            Repository = overrides.RepositoryOverride,
            Branch = overrides.BranchOverride,
            PullRequestNumber = overrides.PullRequestNumberOverride,
            IssueRefs = overrides.IssueRefs.Count == 0 ? null : overrides.IssueRefs.ToArray(),
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

        [JsonPropertyName("issueRefs")]
        public IReadOnlyList<string>? IssueRefs { get; set; }
    }
}
