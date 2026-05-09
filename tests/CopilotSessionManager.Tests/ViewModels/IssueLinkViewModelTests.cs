using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class IssueLinkViewModelTests
{
    private static IssueRef Ref(string slug = "octo/widgets", int n = 42) => new(slug, n);

    [Fact]
    public void Display_SameRepoAsSession_ShowsHashOnly()
    {
        var vm = new IssueLinkViewModel(Ref("octo/widgets", 7), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.Display.Should().Be("#7");
    }

    [Fact]
    public void Display_CrossRepo_ShowsQualifiedForm()
    {
        var vm = new IssueLinkViewModel(Ref("acme/tools", 13), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.Display.Should().Be("acme/tools#13");
    }

    [Fact]
    public void State_DefaultsToUnknown_BadgeBrushIsGray()
    {
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.State.Should().Be(IssueState.Unknown);
        ((SolidColorBrush)vm.BadgeBrush).Color.Should().Be(Color.FromRgb(0x6C, 0x70, 0x86));
    }

    [Fact]
    public void ApplyInfo_OpenIssue_PaintsBadgeGreen()
    {
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.ApplyInfo(new IssueInfo(Ref(), "Add cool feature", IssueState.Open, "https://github.com/octo/widgets/issues/42"));

        vm.State.Should().Be(IssueState.Open);
        vm.Title.Should().Be("Add cool feature");
        ((SolidColorBrush)vm.BadgeBrush).Color.Should().Be(Color.FromRgb(0xA6, 0xE3, 0xA1));
    }

    [Fact]
    public void ApplyInfo_ClosedIssue_PaintsBadgePurple()
    {
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.ApplyInfo(new IssueInfo(Ref(), "Done", IssueState.Closed, "https://github.com/octo/widgets/issues/42"));

        ((SolidColorBrush)vm.BadgeBrush).Color.Should().Be(Color.FromRgb(0xCB, 0xA6, 0xF7));
    }

    [Fact]
    public void Tooltip_WithoutTitle_FallsBackToUrl()
    {
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.Tooltip.Should().Contain("octo/widgets#42");
        vm.Tooltip.Should().Contain("https://github.com/octo/widgets/issues/42");
        vm.Tooltip.Should().Contain("State unknown");
    }

    [Fact]
    public void Tooltip_WithTitle_ShowsTitleAndState()
    {
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", new RecordingLauncher(), _ => Task.CompletedTask);

        vm.ApplyInfo(new IssueInfo(Ref(), "Add cool feature", IssueState.Open, "https://github.com/octo/widgets/issues/42"));

        vm.Tooltip.Should().Contain("Open");
        vm.Tooltip.Should().Contain("Add cool feature");
    }

    [Fact]
    public async Task OpenCommand_LaunchesUrl()
    {
        var launcher = new RecordingLauncher();
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", launcher, _ => Task.CompletedTask);

        await ((CommunityToolkit.Mvvm.Input.AsyncRelayCommand)vm.OpenCommand).ExecuteAsync(null);

        launcher.Calls.Should().ContainSingle().Which.Should().Be("https://github.com/octo/widgets/issues/42");
    }

    [Fact]
    public async Task RemoveCommand_InvokesCallback()
    {
        IssueRef? captured = null;
        var vm = new IssueLinkViewModel(Ref(), "octo/widgets", new RecordingLauncher(),
            r => { captured = r; return Task.CompletedTask; });

        await ((CommunityToolkit.Mvvm.Input.AsyncRelayCommand)vm.RemoveCommand).ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.OwnerRepo.Should().Be("octo/widgets");
        captured.Number.Should().Be(42);
    }

    private sealed class RecordingLauncher : IFileLauncher
    {
        public System.Collections.Generic.List<string> Calls { get; } = new();
        public Task OpenAsync(string path, System.Threading.CancellationToken cancellationToken = default)
        {
            Calls.Add(path);
            return Task.CompletedTask;
        }
    }
}
