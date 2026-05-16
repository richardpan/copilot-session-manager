using System;
using System.IO;

namespace CopilotSessionManager.Terminal.Tests.Conformance;

/// <summary>
/// Finds the repo root from a test-time working directory by walking
/// upward until a marker file (the solution) appears. Lets the
/// conformance harness locate <c>samples/traces/</c> regardless of
/// where the test host launched from.
/// </summary>
internal static class RepoLocator
{
    private const string SolutionFile = "CopilotSessionManager.sln";

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate {SolutionFile} starting from {AppContext.BaseDirectory}");
    }

    public static string SamplesTracesDir() => Path.Combine(FindRepoRoot(), "samples", "traces");
}
