using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Accessibility;

public class ThemeResourceTests
{
    private static readonly string[] RequiredBrushKeys =
    {
        "BackgroundBrush",
        "SurfaceBrush",
        "TextPrimaryBrush",
        "AccentBrush",
        "FocusOutlineBrush",
        "StatusWorkingBrush",
        "StatusIdleBrush",
        "StatusCrashedBrush",
        "StatusAwaitingBrush",
        "BadgeOpenBrush",
        "BadgeClosedBrush",
        "BadgeMergedBrush",
        "BadgeDraftBrush",
        "BadgePendingBrush",
    };

    [Theory]
    [InlineData("CatppuccinMocha.xaml")]
    [InlineData("GitHubDark.xaml")]
    [InlineData("GitHubLight.xaml")]
    [InlineData("HighContrast.xaml")]
    [InlineData("SynthwaveEighties.xaml")]
    public void ThemeDictionary_DefinesRequiredBrushKeys(string fileName)
    {
        RunSta(() =>
        {
            var dictionary = LoadThemeDictionary(fileName);

            foreach (var key in RequiredBrushKeys)
            {
                dictionary.Contains(key).Should().BeTrue($"{fileName} should define {key}");
                dictionary[key].Should().BeAssignableTo<SolidColorBrush>($"{key} should resolve to a brush");
            }
        });
    }

    [Fact]
    public void AppXaml_MergesNamedPaletteByDefault()
    {
        var appXaml = File.ReadAllText(Path.Combine(RepoRoot.FullName, "src", "CopilotSessionManager", "App.xaml"));

        appXaml.Should().Contain("<ResourceDictionary.MergedDictionaries>");
        appXaml.Should().Contain("Themes/GitHubDark.xaml");
    }

    private static ResourceDictionary LoadThemeDictionary(string fileName)
    {
        var path = Path.Combine(RepoRoot.FullName, "src", "CopilotSessionManager", "Themes", fileName);
        File.Exists(path).Should().BeTrue($"expected to find {fileName} at {path}");

        using var stream = File.OpenRead(path);
        return XamlReader.Load(stream).Should().BeOfType<ResourceDictionary>().Subject;
    }

    private static DirectoryInfo RepoRoot
    {
        get
        {
            var assemblyPath = Path.GetDirectoryName(typeof(ThemeResourceTests).Assembly.Location)!;
            var probe = new DirectoryInfo(assemblyPath);
            while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "CopilotSessionManager.sln")))
            {
                probe = probe.Parent;
            }
            probe.Should().NotBeNull("test must be able to find the repo root containing the sln file");
            return probe!;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
