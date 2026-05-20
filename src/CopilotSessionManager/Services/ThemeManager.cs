using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Services;

/// <summary>
/// Swaps the merged theme <see cref="ResourceDictionary"/> at runtime.
/// All <c>DynamicResource</c> bindings update automatically when the
/// dictionary is replaced.
/// </summary>
public sealed class ThemeManager
{
    private static readonly Dictionary<string, string> ThemeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GitHubDark"] = "Themes/GitHubDark.xaml",
        ["GitHubLight"] = "Themes/GitHubLight.xaml",
        ["CatppuccinMocha"] = "Themes/CatppuccinMocha.xaml",
        ["HighContrast"] = "Themes/HighContrast.xaml",
    };

    private readonly ILogger<ThemeManager> _logger;
    private ResourceDictionary? _currentThemeDictionary;

    public ThemeManager(ILogger<ThemeManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the name of the currently active theme.</summary>
    public string CurrentTheme { get; private set; } = "GitHubDark";

    /// <summary>
    /// Returns all supported theme names in display order.
    /// </summary>
    public static IReadOnlyList<string> AvailableThemes { get; } = new[]
    {
        "GitHubDark",
        "GitHubLight",
        "CatppuccinMocha",
        "HighContrast",
    };

    /// <summary>
    /// Apply <paramref name="themeName"/> by replacing the first merged
    /// dictionary in <see cref="Application.Current"/>. Falls back to
    /// <c>GitHubDark</c> when the name is unknown.
    /// </summary>
    public void Apply(string themeName)
    {
        if (!ThemeFiles.ContainsKey(themeName))
        {
            _logger.LogWarning("Unknown theme '{Theme}', falling back to GitHubDark.", themeName);
            themeName = "GitHubDark";
        }

        var uri = new Uri(ThemeFiles[themeName], UriKind.Relative);
        var newDictionary = new ResourceDictionary { Source = uri };

        var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

        if (_currentThemeDictionary is not null)
        {
            mergedDictionaries.Remove(_currentThemeDictionary);
        }
        else if (mergedDictionaries.Count > 0)
        {
            // First call — remove the dictionary loaded by App.xaml.
            mergedDictionaries.RemoveAt(0);
        }

        mergedDictionaries.Insert(0, newDictionary);
        _currentThemeDictionary = newDictionary;
        CurrentTheme = themeName;

        _logger.LogInformation("Theme switched to {Theme}.", themeName);
    }
}
