using System;
using System.Threading;
using System.Threading.Tasks;
using Unosquare.FFME;

namespace CoursePlayer.Services;

/// <summary>
/// Owns the single FFME <see cref="Unosquare.FFME.MediaElement"/> for the whole app and
/// exposes playback as plain async calls plus events. The element itself is handed to the
/// player view, which parents it into its own visual tree so the frames are actually drawn.
/// </summary>
public interface IAssetPlaybackService
{
    /// <summary>The renderer. Null once the service has been disposed.</summary>
    Unosquare.FFME.MediaElement? MediaElement { get; }

    /// <summary>Current playback clock. <see cref="TimeSpan.Zero"/> when nothing is open.</summary>
    TimeSpan Position { get; }

    /// <summary>Total length, or null for live/unseekable streams.</summary>
    TimeSpan? NaturalDuration { get; }

    /// <summary>0..1 read-ahead buffer fill, used to paint the scrubber's buffered track.</summary>
    double BufferingProgress { get; }

    bool IsOpen { get; }

    bool IsPlaying { get; }

    bool IsSeekable { get; }

    /// <summary>Frame size of the open video, or null when there is no video stream.</summary>
    (int Width, int Height)? VideoSize { get; }

    /// <summary>True when the open media carries subtitles (embedded or a sidecar file).</summary>
    bool HasSubtitles { get; }

    /// <summary>
    /// Sidecar subtitle file applied on the next open. Set before <see cref="OpenAsync"/>;
    /// FFME can only attach an external track while the container is being opened.
    /// </summary>
    string? SubtitlesSource { get; set; }

    /// <summary>Live switch: false cancels subtitle rendering without reopening the file.</summary>
    bool AreSubtitlesEnabled { get; set; }

    /// <summary>0..1. Survives across opens.</summary>
    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Playback rate, 1.0 being normal speed. Survives across opens.</summary>
    double SpeedRatio { get; set; }

    Task<bool> OpenAsync(string filePath, CancellationToken cancellationToken = default);

    Task PlayAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Raised on the UI thread once the container is open and the length is known.</summary>
    event EventHandler? MediaOpened;

    /// <summary>Raised on the UI thread when the clock reaches the end of the stream.</summary>
    event EventHandler? MediaEnded;

    /// <summary>Raised on the UI thread when open or decode fails.</summary>
    event EventHandler<Exception>? MediaFailed;
}