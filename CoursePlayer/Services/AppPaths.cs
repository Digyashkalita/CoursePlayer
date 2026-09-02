namespace CoursePlayer.Services;

/// <inheritdoc cref="IAppPaths"/>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Overrides the compiled-in FFmpeg location, for machines that keep it elsewhere.</summary>
    public const string FFmpegDirectoryEnvironmentVariable = "COURSEPLAYER_FFMPEG_DIR";

    private const string DefaultFFmpegDirectory = @"C:\ffmpeg\x64";

    public AppPaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CoursePlayer");

        DatabasePath = Path.Combine(RootDirectory, "courseplayer.db");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
        LogDirectory = Path.Combine(RootDirectory, "logs");
        ThumbnailsDirectory = Path.Combine(RootDirectory, "Assets", "Thumbnails");
        CourseThumbnailsDirectory = Path.Combine(RootDirectory, "Assets", "CourseThumbnails");
        CacheDirectory = Path.Combine(RootDirectory, "cache");

        var overridden = Environment.GetEnvironmentVariable(FFmpegDirectoryEnvironmentVariable);
        FFmpegDirectory = string.IsNullOrWhiteSpace(overridden)
            ? DefaultFFmpegDirectory
            : overridden.Trim();
    }

    public string RootDirectory { get; }

    public string DatabasePath { get; }

    public string SettingsPath { get; }

    public string LogDirectory { get; }

    public string ThumbnailsDirectory { get; }

    public string CourseThumbnailsDirectory { get; }

    public string CacheDirectory { get; }

    public string FFmpegDirectory { get; }

    public string GetAssetThumbnailDirectory(int assetId) =>
        Path.Combine(ThumbnailsDirectory, assetId.ToString());

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(CourseThumbnailsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
