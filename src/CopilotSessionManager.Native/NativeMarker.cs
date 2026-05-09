using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CopilotSessionManager.Native.Tests")]

// Placeholder marker for the Native assembly.
// Real ConPTY / Win32 P/Invoke types will live alongside this file; see
// docs/adr/0001-conpty-for-embedded-terminal.md.

namespace CopilotSessionManager.Native;

/// <summary>
/// Internal marker type for the Native assembly. Will be removed once real
/// types land here.
/// </summary>
internal static class NativeMarker
{
    /// <summary>The friendly name of this assembly, for diagnostics.</summary>
    public const string Name = "CopilotSessionManager.Native";
}
