using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.Services;

/// <summary>User preferences that persist across launches. Currently just the chosen theme.</summary>
public sealed record AppSettings
{
    /// <summary>Id of the selected <see cref="ThemePalette"/>; null falls back to the default.</summary>
    public string? ThemeId { get; init; }
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Reads never throw: a missing or
/// corrupt file yields defaults so a bad settings file can never stop the app starting.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    AppSettings Load();

    void Save(AppSettings settings);
}

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(IAppPaths paths, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsPath))
            {
                var json = File.ReadAllText(_paths.SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read settings from {Path}; using defaults.", _paths.SettingsPath);
        }

        Current = new AppSettings();
        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = settings;

        try
        {
            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(_paths.SettingsPath, json);
        }
        catch (Exception ex)
        {
            // A failed write must not disrupt the UI; the choice just won't survive a restart.
            _logger.LogError(ex, "Could not save settings to {Path}.", _paths.SettingsPath);
        }
    }
}
