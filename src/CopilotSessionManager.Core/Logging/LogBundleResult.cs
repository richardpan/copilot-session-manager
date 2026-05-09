namespace CopilotSessionManager.Core.Logging;

/// <summary>
/// Result of a successful <see cref="ILogBundler.BundleAsync"/> call.
/// </summary>
/// <param name="DestinationPath">Absolute path to the produced zip file.</param>
/// <param name="FileCount">Number of <c>*.log</c> files included in the bundle
/// (excludes the synthesized <c>manifest.txt</c>).</param>
/// <param name="TotalBytes">Total size in bytes of the produced zip on disk.</param>
public sealed record LogBundleResult(string DestinationPath, int FileCount, long TotalBytes);
