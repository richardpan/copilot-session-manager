using System.IO;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Views;

/// <summary>
/// V1.7 (#120) source-pinning tests for the comprehensive button-tooltip
/// coverage in <c>MainWindow.xaml</c> and <c>OnboardingWindow.xaml</c>.
///
/// The user explicitly asked for hover discoverability on every button and
/// pointed at <c>Clean stale locks</c> as the gold-standard answer. These
/// tests pin a small set of phrases that must survive future refactors so we
/// never silently regress to terse one-liners or a missing tooltip.
/// </summary>
public class MainWindowTooltipTests
{
    [Fact]
    public void Toolbar_RefreshButton_HasRichTooltip()
    {
        var xaml = ReadMainWindowXaml();

        // The toolbar Refresh button shipped without a ToolTip in V1.6; V1.7
        // adds a multi-line one explaining what a refresh does and that it is
        // rarely needed because the dashboard auto-updates.
        xaml.Should().Contain("Content=\"↻ Refresh\"",
            "the toolbar Refresh button must still exist with its glyph");
        xaml.Should().Contain("\"Refresh\"",
            "the Refresh tooltip must use 'Refresh' as its bold heading");
        xaml.Should().Contain("auto",
            "the Refresh tooltip must explain that the dashboard already updates automatically");
    }

    [Fact]
    public void Toolbar_BulkCleanLocks_TooltipExplainsWhatAStaleLockIs()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("Clean stale locks (all sessions)",
            "the toolbar bulk Clean tooltip must use the bulk-scope heading");
        // Phrases that explain WHY a stale lock exists and HOW the cleanup
        // discriminates live vs. dead locks. These are the user-visible parts
        // that must not regress.
        xaml.Should().Contain("inuse.&lt;pid&gt;.lock",
            "the bulk Clean tooltip must reference the actual lock filename pattern");
        xaml.Should().Contain("crashes",
            "the bulk Clean tooltip must mention crashes as the cause of stale locks");
        xaml.Should().Contain("safe to click",
            "the bulk Clean tooltip must reassure the user it never touches live locks");
    }

    [Fact]
    public void PerRow_CleanStaleLocks_TooltipFullyExplainsBehaviour()
    {
        // This is THE marquee tooltip from #120 — when the user asked
        // "what does Clean stale locks do?" the answer below is what hover
        // must now show. We pin the four key concepts: the file name pattern,
        // why locks become stale, that only orphaned locks are deleted, and
        // the relationship to the Resume button.
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Clean stale locks\"",
            "the per-row tooltip must use 'Clean stale locks' as its bold heading");
        xaml.Should().Contain("inuse.&lt;pid&gt;.lock",
            "the per-row tooltip must reference the actual lock filename pattern");
        xaml.Should().Contain("crashes, is force-killed, or the machine power-cycles",
            "the per-row tooltip must enumerate the three causes of stale locks");
        xaml.Should().Contain("process that is no longer running",
            "the per-row tooltip must state that stale locks point at dead processes");
        xaml.Should().Contain("only the orphaned ones",
            "the per-row tooltip must reassure the user that live locks are never deleted");
        xaml.Should().Contain("▶ Resume runs this same cleanup",
            "the per-row tooltip must mention that Resume already cleans up automatically");
    }

    [Fact]
    public void PerRow_ResumeButton_TooltipExplainsWhatCrashedMeans()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Resume crashed session\"",
            "the Resume tooltip must use 'Resume crashed session' as its bold heading");
        xaml.Should().Contain("dead PID",
            "the Resume tooltip must explain that 'crashed' means csm found a lock pointing at a dead PID");
    }

    [Fact]
    public void PerRow_DeleteButton_TooltipWarnsThatItIsPermanent()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Delete session from disk\"",
            "the Delete tooltip must use 'Delete session from disk' as its bold heading");
        xaml.Should().Contain("cannot be undone",
            "the Delete tooltip must warn that deletion is irreversible");
        xaml.Should().Contain("local overrides",
            "the Delete tooltip must mention that csm-side metadata (rename, star, README cache, …) is also cleared");
    }

    [Fact]
    public void PerRow_RenameButton_TooltipExplainsItIsLocalOnly()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Rename session\"",
            "the Rename tooltip must use 'Rename session' as its bold heading");
        xaml.Should().Contain("Esc",
            "the Rename tooltip must mention the keyboard shortcuts (Enter/Esc)");
        xaml.Should().Contain("never modify the Copilot session",
            "the Rename tooltip must reassure the user that renames don't disturb the agent");
    }

    [Fact]
    public void PerRow_OpenButton_TooltipMentionsForegrounding()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Open in PowerShell\"",
            "the Open tooltip must use 'Open in PowerShell' as its bold heading");
        xaml.Should().Contain("brings it to the foreground",
            "the Open tooltip must explain the bring-to-front behaviour for already-open windows");
    }

    [Fact]
    public void PerRow_DocsButton_TooltipMentionsScaffoldingOnFirstClick()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Open session documentation\"",
            "the Docs tooltip must use 'Open session documentation' as its bold heading");
        xaml.Should().Contain("scaffolds an empty SESSION-DOCS.md",
            "the Docs tooltip must explain the first-click scaffolding behaviour");
        xaml.Should().Contain("never overwrites your edits",
            "the Docs tooltip must reassure the user that csm preserves their edits");
    }

    [Fact]
    public void Toolbar_NewSessionButton_HasRichTooltip()
    {
        var xaml = ReadMainWindowXaml();

        xaml.Should().Contain("\"Start a new Copilot session\"",
            "the + New session tooltip must use 'Start a new Copilot session' as its bold heading");
        xaml.Should().Contain("embedded terminal tab",
            "the + New session tooltip must describe the default embedded-tab launch route");
        xaml.Should().Contain("Right-click to launch in an external PowerShell window",
            "the + New session tooltip must point users at the right-click context menu for the external fallback");
    }

    [Fact]
    public void OnboardingWindow_InstallButton_HasTooltip()
    {
        var xaml = ReadOnboardingWindowXaml();

        xaml.Should().Contain(
            "ToolTip=\"Open this prerequisite's install instructions in your default browser. Re-check after installing.\"",
            "the Install button on the onboarding wizard must declare a hover tooltip");
    }

    [Fact]
    public void OnboardingWindow_RecheckButton_HasTooltip()
    {
        var xaml = ReadOnboardingWindowXaml();

        xaml.Should().Contain(
            "ToolTip=\"Re-run the prerequisite checks (PowerShell, Copilot CLI, gh CLI) after installing or updating one of them.\"",
            "the 🔄 Re-check button on the onboarding wizard must declare a hover tooltip");
    }

    private static string ReadMainWindowXaml() =>
        File.ReadAllText(LocateXaml("MainWindow.xaml"));

    private static string ReadOnboardingWindowXaml() =>
        File.ReadAllText(LocateXaml("OnboardingWindow.xaml"));

    private static string LocateXaml(string fileName)
    {
        var assemblyPath = Path.GetDirectoryName(typeof(MainWindowTooltipTests).Assembly.Location)!;
        var probe = new DirectoryInfo(assemblyPath);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "CopilotSessionManager.sln")))
        {
            probe = probe.Parent;
        }
        probe.Should().NotBeNull("test must be able to find the repo root containing the sln file");
        var path = Path.Combine(probe!.FullName, "src", "CopilotSessionManager", fileName);
        File.Exists(path).Should().BeTrue($"expected to find {fileName} at {path}");
        return path;
    }
}
