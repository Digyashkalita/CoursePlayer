namespace CoursePlayer.Services;

/// <summary>
/// Every writable location the app uses. All of it lives under one per-user folder so
/// "Clear Cache" and uninstall are a single directory delete.
/// </summary>
public interface IAppPaths
{
    /// <summary>%LOCALAPPDATA%\CoursePlayer</summary>
    string RootDirectory { get; }

    string DatabasePath { get; }

    /// <summary>%LOCALAPPDATA%\CoursePlayer\settings.json — user preferences (theme, etc.).</summary>
    string SettingsPath { get; }

    string LogDirectory { get; }

    /// <summary>Scrubber hover frames, one subfolder per asset id.</summary>
    string ThumbnailsDirectory { get; }

    /// <summary>Generated and uploaded 16:9 course covers.</summary>
    string CourseThumbnailsDirectory { get; }

    string CacheDirectory { get; }

    /// <summary>Directory holding the shared FFmpeg binaries FFME loads.</summary>
    string FFmpegDirectory { get; }

    /// <summary>Folder holding the hover frames for a single asset.</summary>
    string GetAssetThumbnailDirectory(int assetId);

    void EnsureCreated();
}
