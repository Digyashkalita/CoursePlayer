using Microsoft.Extensions.Logging;
using Unosquare.FFME;

namespace CoursePlayer.Services;

/// <summary>
/// Points FFME at the shared FFmpeg binaries and reports, without throwing, whether
/// they are actually usable. Media views consult <see cref="IsAvailable"/> so a missing
/// FFmpeg install degrades to a clear message instead of a crash on first play.
/// </summary>
public interface IFFmpegBootstrapper
{
    bool IsAvailable { get; }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false; empty otherwise.</summary>
    string StatusMessage { get; }

    string Directory { get; }

    /// <summary>Sets the FFME search path and validates it. Safe to call once at startup.</summary>
    void Initialize();

    /// <summary>Loads FFmpeg off the UI thread so the first video opens without a stall.</summary>
    Task PreloadAsync();
}

/// <inheritdoc cref="IFFmpegBootstrapper"/>
public sealed class FFmpegBootstrapper : IFFmpegBootstrapper
{
    /// <summary>
    /// FFME 4.4.350 binds to FFmpeg.AutoGen 4.4, which loads these exact sonames.
    /// FFmpeg 5/6/7 ship higher version suffixes (avcodec-59/60/61) and will NOT load.
    /// </summary>
    private static readonly string[] RequiredLibraries =
    [
        "avcodec-58.dll",
        "avdevice-58.dll",
        "avfilter-7.dll",
        "avformat-58.dll",
        "avutil-56.dll",
        "swresample-3.dll",
        "swscale-5.dll",
    ];

    private readonly IAppPaths _paths;
    private readonly ILogger<FFmpegBootstrapper> _logger;

    public FFmpegBootstrapper(IAppPaths paths, ILogger<FFmpegBootstrapper> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public string Directory => _paths.FFmpegDirectory;

    public void Initialize()
    {
        var directory = _paths.FFmpegDirectory;

        try
        {
            // Setting this after FFmpeg has loaded throws, so it must happen at startup.
            // The load mode is left at FFME's default (full features).
            Library.FFmpegDirectory = directory;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StatusMessage = $"Could not configure FFmpeg directory '{directory}': {ex.Message}";
            _logger.LogError(ex, "Failed to set FFME FFmpeg directory to {Directory}.", directory);
            return;
        }

        if (!System.IO.Directory.Exists(directory))
        {
            IsAvailable = false;
            StatusMessage =
                $"FFmpeg not found at '{directory}'. Video playback and metadata are disabled. " +
                $"Place the FFmpeg 4.4 shared (not static) x64 binaries there, or set the " +
                $"{AppPaths.FFmpegDirectoryEnvironmentVariable} environment variable.";
            _logger.LogWarning("FFmpeg directory {Directory} does not exist.", directory);
            return;
        }

        var missing = RequiredLibraries
            .Where(name => !File.Exists(Path.Combine(directory, name)))
            .ToArray();

        if (missing.Length > 0)
        {
            IsAvailable = false;
            StatusMessage =
                $"FFmpeg at '{directory}' is missing {string.Join(", ", missing)}. " +
                $"CoursePlayer needs the FFmpeg 4.4 shared x64 build (FFME 4.4 binds avcodec-58).";
            _logger.LogWarning(
                "FFmpeg directory {Directory} is missing {Missing}.",
                directory,
                string.Join(", ", missing));
            return;
        }

        IsAvailable = true;
        StatusMessage = string.Empty;
        _logger.LogInformation("FFmpeg located at {Directory}.", directory);
    }

    public async Task PreloadAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            var loaded = await Library.LoadFFmpegAsync();
            if (!loaded)
            {
                IsAvailable = false;
                StatusMessage =
                    $"FFmpeg at '{Directory}' could not be loaded. " +
                    "Confirm it is the 64-bit FFmpeg 4.4 shared build.";
                _logger.LogError("Library.LoadFFmpegAsync returned false for {Directory}.", Directory);
                return;
            }

            _logger.LogInformation(
                "FFmpeg loaded (version info: {Info}).",
                Library.FFmpegVersionInfo ?? "unknown");
        }
        catch (Exception ex)
        {
            // A present-but-broken install (wrong bitness, wrong major version) lands here.
            IsAvailable = false;
            StatusMessage =
                $"FFmpeg at '{Directory}' failed to load: {ex.Message}. " +
                "Confirm it is the 64-bit FFmpeg 4.4 shared build.";
            _logger.LogError(ex, "FFmpeg failed to load from {Directory}.", Directory);
        }
    }
}
