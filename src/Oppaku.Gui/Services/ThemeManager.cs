using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Oppaku.Gui.Themes;

namespace Oppaku.Gui.Services;

public static class ThemeManager
{
    private static readonly string SettingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
    private static AppSettings _settings = new();

    public static ThemePreset CurrentTheme => _settings.Theme;
    public static event Action<ThemePreset>? ThemeChanged;

    public static void Initialize()
    {
        LoadSettings();
        ApplyTheme(_settings.Theme, save: false);
    }

    public static void ApplyTheme(ThemePreset preset, bool save = true)
    {
        _settings.Theme = preset;

        string themeFileName = preset switch
        {
            ThemePreset.SandstoneLight => "SandstoneLightTheme.xaml",
            ThemePreset.ObsidianSlate  => "ObsidianSlateTheme.xaml",
            _                          => "EspressoDarkTheme.xaml"
        };

        var uri = new Uri($"pack://application:,,,/Oppaku;component/Themes/{themeFileName}", UriKind.Absolute);
        var newThemeDict = new ResourceDictionary { Source = uri };

        var appResources = Application.Current.Resources;
        
        // 1. Update keys directly in Application.Current.Resources - forces instant WPF DynamicResource re-evaluation
        foreach (var key in newThemeDict.Keys)
        {
            appResources[key] = newThemeDict[key];
        }

        // 2. Synchronize MergedDictionaries
        var existingThemeDicts = appResources.MergedDictionaries
            .Where(d => d.Source != null && d.Source.OriginalString.Contains("Theme", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var dict in existingThemeDicts)
        {
            appResources.MergedDictionaries.Remove(dict);
        }
        appResources.MergedDictionaries.Insert(0, newThemeDict);

        if (save)
        {
            SaveSettings();
        }

        ThemeChanged?.Invoke(preset);
    }

    private static void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    private static void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore settings write errors in read-only environments
        }
    }
}
