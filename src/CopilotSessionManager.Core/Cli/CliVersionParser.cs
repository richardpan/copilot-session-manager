using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CopilotSessionManager.Core.Cli;

public static class CliVersionParser
{
    private static readonly Regex VersionRegex = new(@"(?<!\d)(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? output, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var lines = output.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            var match = VersionRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var minor = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var patch = match.Groups[3].Success
                ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            version = new Version(major, minor, patch);
            return true;
        }

        return false;
    }
}
