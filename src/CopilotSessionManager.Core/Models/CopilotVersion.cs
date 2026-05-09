using System.Globalization;

namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Lightweight Major.Minor.Patch version of the Copilot CLI as reported in the
/// <c>session.start</c> event. Pre-release suffixes are accepted but ignored
/// for ordering.
/// </summary>
public readonly record struct CopilotVersion(int Major, int Minor, int Patch)
    : IComparable<CopilotVersion>
{
    public static readonly CopilotVersion Zero = new(0, 0, 0);

    /// <summary>
    /// Parse a version string. Accepts <c>"1.0.43"</c> and ignores any
    /// pre-release suffix (e.g. <c>"1.0.43-beta"</c>).
    /// </summary>
    public static bool TryParse(string? value, out CopilotVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Trim();
        var dashIndex = core.IndexOf('-');
        if (dashIndex >= 0)
        {
            core = core[..dashIndex];
        }

        var parts = core.Split('.');
        if (parts.Length < 1 || parts.Length > 3)
        {
            return false;
        }

        var major = 0;
        var minor = 0;
        var patch = 0;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major) || major < 0)
        {
            return false;
        }

        if (parts.Length > 1 &&
            (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor) || minor < 0))
        {
            return false;
        }

        if (parts.Length > 2 &&
            (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch) || patch < 0))
        {
            return false;
        }

        version = new CopilotVersion(major, minor, patch);
        return true;
    }

    public static CopilotVersion Parse(string value) =>
        TryParse(value, out var v)
            ? v
            : throw new FormatException($"Not a valid Copilot CLI version: '{value}'.");

    public int CompareTo(CopilotVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        return Patch.CompareTo(other.Patch);
    }

    public static bool operator <(CopilotVersion a, CopilotVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(CopilotVersion a, CopilotVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(CopilotVersion a, CopilotVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(CopilotVersion a, CopilotVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");
}
