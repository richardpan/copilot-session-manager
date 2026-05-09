using System;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel CreateSut() =>
        new(NullLogger<MainWindowViewModel>.Instance);

    [Fact]
    public void Title_DefaultsToProductAndVersion()
    {
        var sut = CreateSut();
        sut.Title.Should().Contain("Copilot Session Manager");
    }

    [Fact]
    public void HeaderText_DefaultsToProductName()
    {
        var sut = CreateSut();
        sut.HeaderText.Should().Be("Copilot Session Manager");
    }

    [Fact]
    public void WelcomeText_HasDefault()
    {
        var sut = CreateSut();
        sut.WelcomeText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void StatusBarText_HasDefault()
    {
        var sut = CreateSut();
        sut.StatusBarText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Property_RaisesPropertyChanged_WhenSet()
    {
        var sut = CreateSut();
        var raised = false;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.WelcomeText))
            {
                raised = true;
            }
        };

        sut.WelcomeText = "changed";

        raised.Should().BeTrue();
    }
}
