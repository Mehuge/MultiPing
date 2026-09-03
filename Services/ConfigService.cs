using System;
using System.IO;
using System.Text.Json;
using MultiPing.Models;

namespace MultiPing.Services;

/// <summary>Loads and saves <see cref="AppConfig"/> as JSON under the per-user AppData folder.</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>%AppData%\MultiPing</summary>
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiPing");

    public string ConfigPath => Path.Combine(AppDataDirectory, "config.json");

    /// <summary>Default folder for log files when the user has not chosen one.</summary>
    public static string DefaultLogDirectory => Path.Combine(AppDataDirectory, "logs");

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // Corrupt or unreadable config falls back to defaults rather than crashing startup.
        }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Persisting settings is best-effort; failure should not interrupt the user.
        }
    }
}
