using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Accessibility;

/// <summary>
/// A11y audit (#45): the issue badge colour is paired with a non-colour
/// glyph so colour-blind users can still distinguish open vs. closed.
/// Also pins the screen-reader-friendly automation name format.
/// </summary>
public class IssueLinkViewModelAccessibilityTests
{
    private static IssueRef Ref(string slug = "octo/widgets", int n = 42) => new(slug, n);

    private static IssueLinkViewModel Build(IssueRef? r = null) =>
        new(r ?? Ref(), "octo/widgets", new NullLauncher(), _ => Task.CompletedTask);

    [Fact]
    public void BadgeGlyph_DefaultsToUnknownDash()
    {
        Build().BadgeGlyph.Should().Be("–");
    }

    [Fact]
    public void BadgeGlyph_OpenIssue_IsFilledCircle()
    {
        var vm = Build();
        vm.ApplyInfo(new IssueInfo(Ref(), "Title", IssueState.Open, "https://example/1"));

        vm.BadgeGlyph.Should().Be("●");
    }

    [Fact]
    public void BadgeGlyph_ClosedIssue_IsHollowCircle()
    {
        var vm = Build();
        vm.ApplyInfo(new IssueInfo(Ref(), "Title", IssueState.Closed, "https://example/1"));

        vm.BadgeGlyph.Should().Be("○");
    }

    [Fact]
    public void BadgeGlyph_DistinctPerState_NoTwoStatesShareGlyph()
    {
        var open = Build();
        open.ApplyInfo(new IssueInfo(Ref(), "T", IssueState.Open, "u"));
        var closed = Build();
        closed.ApplyInfo(new IssueInfo(Ref(), "T", IssueState.Closed, "u"));
        var unknown = Build();

        var glyphs = new[] { open.BadgeGlyph, closed.BadgeGlyph, unknown.BadgeGlyph };
        glyphs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AutomationName_UnknownState_PrefixesIssueAndShowsQualifiedRef()
    {
        var vm = Build();

        vm.AutomationName.Should().Be("Issue (state unknown) octo/widgets#42");
    }

    [Fact]
    public void AutomationName_OpenIssueWithTitle_IncludesStateAndTitle()
    {
        var vm = Build();
        vm.ApplyInfo(new IssueInfo(Ref(), "Add cool feature", IssueState.Open, "https://example/1"));

        vm.AutomationName.Should().Be("Open issue octo/widgets#42 — Add cool feature");
    }

    [Fact]
    public void AutomationName_ClosedIssueWithoutTitle_OmitsTitleSegment()
    {
        var vm = Build();
        vm.ApplyInfo(new IssueInfo(Ref(), string.Empty, IssueState.Closed, "https://example/1"));

        vm.AutomationName.Should().Be("Closed issue octo/widgets#42");
    }

    [Fact]
    public void StateChange_RaisesPropertyChangedForBadgeGlyphAndAutomationName()
    {
        var vm = Build();
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ApplyInfo(new IssueInfo(Ref(), "X", IssueState.Open, "u"));

        raised.Should().Contain(nameof(IssueLinkViewModel.BadgeGlyph));
        raised.Should().Contain(nameof(IssueLinkViewModel.AutomationName));
    }

    private sealed class NullLauncher : IFileLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
