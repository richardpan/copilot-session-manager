using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CopilotSessionManager.Native.Tests")]

namespace CopilotSessionManager.Native;

/// <summary>
/// Internal marker type for the Native assembly. Kept as a stable handle for
/// tests that want to identify the assembly without referencing P/Invoke types.
/// </summary>
internal static class NativeMarker
{
    /// <summary>The friendly name of this assembly, for diagnostics.</summary>
    public const string Name = "CopilotSessionManager.Native";
}
