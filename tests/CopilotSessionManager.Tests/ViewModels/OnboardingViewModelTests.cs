using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Onboarding;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Core.Settings;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class OnboardingViewModelTests
{
    private static OnboardingViewModel BuildSut(
        FakeChecker? checker = null,
        FakeSettingsStore? store = null,
        SessionsViewModelTests.FakeDiscoveryService? discovery = null,
        SessionsViewModelTests.FakeFileLauncher? launcher = null)
    {
        return new OnboardingViewModel(
            checker ?? new FakeChecker(),
            store ?? new FakeSettingsStore(),
            discovery ?? new SessionsViewModelTests.FakeDiscoveryService(Array.Empty<Session>()),
            launcher ?? new SessionsViewModelTests.FakeFileLauncher(),
            NullLogger<OnboardingViewModel>.Instance);
    }

    [Fact]
    public void InitialState_StartsAtWelcome()
    {
        var vm = BuildSut();
        vm.CurrentStep.Should().Be(OnboardingStep.Welcome);
        vm.StepNumber.Should().Be(1);
        vm.IsCheckingPrerequisites.Should().BeFalse();
        vm.PrerequisiteResults.Should().BeEmpty();
        vm.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Next_FromWelcome_AdvancesToPrerequisitesAndRunsChecks()
    {
        var checker = new FakeChecker(new[]
        {
            new PrerequisiteResult("PowerShell 7+", PrerequisiteStatus.Ok, "ok", null),
        });
        var vm = BuildSut(checker: checker);

        await vm.NextCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(OnboardingStep.Prerequisites);
        checker.CallCount.Should().Be(1);
        vm.PrerequisiteResults.Should().HaveCount(1);
        vm.PrerequisiteResults[0].StatusGlyph.Should().Be("✓");
    }

    [Fact]
    public async Task Next_FromPrerequisites_AdvancesToAdoptionAndLoadsPreview()
    {
        var sessions = new[]
        {
            BuildSession("aaaaaaaaaa", "first", "owner/r1"),
            BuildSession("bbbbbbbbbb", "second", "owner/r2"),
        };
        var discovery = new SessionsViewModelTests.FakeDiscoveryService(sessions);
        var vm = BuildSut(discovery: discovery);
        vm.CurrentStep = OnboardingStep.Prerequisites;

        await vm.NextCommand.ExecuteAsync(null);

        vm.CurrentStep.Should().Be(OnboardingStep.Adoption);
        vm.ExistingSessionCount.Should().Be(2);
        vm.AdoptionPreview.Should().HaveCount(2);
        vm.AdoptionPreview[0].ShortId.Should().Be("aaaaaaaa");
        vm.AdoptionPreview[0].Title.Should().Be("first");
    }

    [Fact]
    public async Task AdoptionPreview_CapsAtFiveSessions()
    {
        var sessions = Enumerable.Range(0, 12)
            .Select(i => BuildSession($"id{i:00}xxxxxx", $"s{i}", "owner/r"))
            .ToArray();
        var vm = BuildSut(discovery: new SessionsViewModelTests.FakeDiscoveryService(sessions));
        vm.CurrentStep = OnboardingStep.Prerequisites;

        await vm.NextCommand.ExecuteAsync(null);

        vm.ExistingSessionCount.Should().Be(12);
        vm.AdoptionPreview.Should().HaveCount(5);
    }

    [Fact]
    public async Task Next_FromAdoption_PersistsCompletionAndSignalsClose()
    {
        var store = new FakeSettingsStore();
        var vm = BuildSut(store: store);
        vm.CurrentStep = OnboardingStep.Adoption;

        await vm.NextCommand.ExecuteAsync(null);

        store.LastSaved!.OnboardingCompleted.Should().BeTrue();
        vm.IsComplete.Should().BeTrue();
        vm.CurrentStep.Should().Be(OnboardingStep.Done);
    }

    [Fact]
    public void Back_DecrementsStep_AndStopsAtWelcome()
    {
        var vm = BuildSut();
        vm.CurrentStep = OnboardingStep.Adoption;

        vm.BackCommand.Execute(null);
        vm.CurrentStep.Should().Be(OnboardingStep.Prerequisites);

        vm.BackCommand.Execute(null);
        vm.CurrentStep.Should().Be(OnboardingStep.Welcome);

        vm.BackCommand.Execute(null);
        vm.CurrentStep.Should().Be(OnboardingStep.Welcome);
    }

    [Fact]
    public async Task Skip_FromAnyStep_PersistsAndCompletes()
    {
        var store = new FakeSettingsStore();
        var vm = BuildSut(store: store);
        vm.CurrentStep = OnboardingStep.Prerequisites;

        await vm.SkipCommand.ExecuteAsync(null);

        store.LastSaved!.OnboardingCompleted.Should().BeTrue();
        vm.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Recheck_ReplacesPriorResults()
    {
        var checker = new FakeChecker(new[]
        {
            new PrerequisiteResult("a", PrerequisiteStatus.Failed, "no", "https://example.com"),
        });
        var vm = BuildSut(checker: checker);

        await vm.RecheckPrerequisitesCommand.ExecuteAsync(null);
        vm.PrerequisiteResults.Should().HaveCount(1);

        checker.SetNext(new[]
        {
            new PrerequisiteResult("a", PrerequisiteStatus.Ok, "yes", null),
            new PrerequisiteResult("b", PrerequisiteStatus.Warning, "maybe", null),
        });
        await vm.RecheckPrerequisitesCommand.ExecuteAsync(null);

        vm.PrerequisiteResults.Should().HaveCount(2);
        vm.PrerequisiteResults[0].Status.Should().Be(PrerequisiteStatus.Ok);
    }

    [Fact]
    public async Task OpenInstallUrl_DelegatesToFileLauncher()
    {
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = BuildSut(launcher: launcher);

        await vm.OpenInstallUrlCommand.ExecuteAsync("https://example.com/install");

        launcher.Calls.Should().ContainSingle().Which.Should().Be("https://example.com/install");
    }

    [Fact]
    public async Task OpenInstallUrl_NullOrWhitespace_NoOp()
    {
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = BuildSut(launcher: launcher);

        await vm.OpenInstallUrlCommand.ExecuteAsync(null);
        await vm.OpenInstallUrlCommand.ExecuteAsync("   ");

        launcher.Calls.Should().BeEmpty();
    }

    [Fact]
    public void HasFailedPrerequisites_ReflectsAnyFailure()
    {
        var vm = BuildSut();
        vm.HasFailedPrerequisites.Should().BeFalse();

        vm.PrerequisiteResults.Add(new PrerequisiteResultViewModel(
            new PrerequisiteResult("x", PrerequisiteStatus.Ok, "ok", null)));
        vm.HasFailedPrerequisites.Should().BeFalse();

        vm.PrerequisiteResults.Add(new PrerequisiteResultViewModel(
            new PrerequisiteResult("y", PrerequisiteStatus.Failed, "bad", null)));
        // The property recomputes on demand from the collection.
        vm.HasFailedPrerequisites.Should().BeTrue();
    }

    [Fact]
    public void StepNumber_TracksCurrentStep()
    {
        var vm = BuildSut();
        vm.StepNumber.Should().Be(1);

        vm.CurrentStep = OnboardingStep.Prerequisites;
        vm.StepNumber.Should().Be(2);

        vm.CurrentStep = OnboardingStep.Adoption;
        vm.StepNumber.Should().Be(3);
    }

    [Fact]
    public async Task Recheck_ContinuesGracefullyWhenCheckerThrows()
    {
        var checker = new FakeChecker { ShouldThrow = true };
        var vm = BuildSut(checker: checker);

        await vm.RecheckPrerequisitesCommand.ExecuteAsync(null);

        vm.IsCheckingPrerequisites.Should().BeFalse();
        vm.PrerequisiteResults.Should().BeEmpty();
    }

    private static Session BuildSession(string id, string summary, string repo) =>
        new(
            Id: id,
            Cwd: @"C:\ws\fake",
            Repository: repo,
            Branch: "main",
            Summary: summary,
            HostType: "claude",
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt: DateTimeOffset.UtcNow,
            TurnCount: 1,
            Status: SessionStatus.Working,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>(),
            ModelInfo: null,
            GitHubLinks: null);

    private sealed class FakeChecker : IPrerequisiteChecker
    {
        private IReadOnlyList<PrerequisiteResult> _next;
        public int CallCount { get; private set; }
        public bool ShouldThrow { get; set; }

        public FakeChecker(IReadOnlyList<PrerequisiteResult>? results = null)
        {
            _next = results ?? Array.Empty<PrerequisiteResult>();
        }

        public void SetNext(IReadOnlyList<PrerequisiteResult> results) => _next = results;

        public Task<IReadOnlyList<PrerequisiteResult>> CheckAllAsync(CancellationToken ct = default)
        {
            CallCount++;
            if (ShouldThrow)
                throw new InvalidOperationException("simulated");
            return Task.FromResult(_next);
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public AppSettings? LastSaved { get; private set; }
        public AppSettings Current { get; set; } = AppSettings.Defaults();

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            LastSaved = settings;
            Current = settings;
            return Task.CompletedTask;
        }
    }
}
