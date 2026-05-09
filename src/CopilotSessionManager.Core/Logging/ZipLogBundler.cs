using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Logging;

/// <summary>
/// Default <see cref="ILogBundler"/> backed by
/// <see cref="System.IO.Compression.ZipArchive"/>. Reads from
/// <see cref="AppPaths.LogsDirectory"/> and writes the zip atomically by
/// staging to <c>{destinationPath}.tmp</c> and then moving into place.
/// </summary>
public sealed class ZipLogBundler : ILogBundler
{
    private readonly string _logsDirectory;
    private readonly ILogger<ZipLogBundler> _logger;

    public ZipLogBundler(ILogger<ZipLogBundler>? logger = null)
        : this(AppPaths.LogsDirectory, logger)
    {
    }

    /// <summary>
    /// Test-friendly constructor allowing the caller to override the source
    /// logs directory.
    /// </summary>
    public ZipLogBundler(string logsDirectory, ILogger<ZipLogBundler>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory))
        {
            throw new ArgumentException("Logs directory must be a non-empty path.", nameof(logsDirectory));
        }

        _logsDirectory = logsDirectory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ZipLogBundler>.Instance;
    }

    public async Task<LogBundleResult> BundleAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path must be non-empty.", nameof(destinationPath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var tempPath = destinationPath + ".tmp";
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        var fileCount = 0;
        try
        {
            using (var fs = File.Create(tempPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                // Manifest first so it appears at the top of any unzipper.
                var manifestEntry = zip.CreateEntry("manifest.txt", CompressionLevel.Optimal);
                using (var ms = manifestEntry.Open())
                using (var sw = new StreamWriter(ms, Encoding.UTF8))
                {
                    await sw.WriteLineAsync($"Copilot Session Manager log bundle").ConfigureAwait(false);
                    await sw.WriteLineAsync($"Generated:    {DateTimeOffset.UtcNow:O}").ConfigureAwait(false);
                    await sw.WriteLineAsync($"App version:  {AppMetadata.Version}").ConfigureAwait(false);
                    await sw.WriteLineAsync($"Product:      {AppMetadata.ProductName}").ConfigureAwait(false);
                    await sw.WriteLineAsync($"OS:           {RuntimeInformation.OSDescription}").ConfigureAwait(false);
                    await sw.WriteLineAsync($"Architecture: {RuntimeInformation.OSArchitecture}").ConfigureAwait(false);
                    await sw.WriteLineAsync($"Source dir:   {_logsDirectory}").ConfigureAwait(false);
                    await sw.WriteLineAsync().ConfigureAwait(false);
                    await sw.WriteLineAsync("Logs already had PII / secret patterns redacted at write time.")
                        .ConfigureAwait(false);
                }

                if (Directory.Exists(_logsDirectory))
                {
                    var files = Directory.EnumerateFiles(_logsDirectory, "*.log", SearchOption.TopDirectoryOnly)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var entryName = "logs/" + Path.GetFileName(file);
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var src = new FileStream(
                                file,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var dst = entry.Open();
                            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                            fileCount++;
                        }
                        catch (IOException ioex)
                        {
                            // A log file might be locked by the file sink; skip it but record the
                            // failure so the user knows what's missing from the bundle.
                            _logger.LogWarning(ioex, "Skipped log file {Path} while bundling.", file);
                        }
                    }
                }
            }

            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }

            var size = new FileInfo(destinationPath).Length;
            _logger.LogInformation(
                "Bundled {FileCount} log file(s) ({Bytes} bytes) to {Destination}.",
                fileCount,
                size,
                destinationPath);
            return new LogBundleResult(destinationPath, fileCount, size);
        }
        catch
        {
            // Best-effort cleanup of the temp on failure.
            try
            { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* swallow */ }
            throw;
        }
    }
}
